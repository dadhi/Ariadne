﻿// © 2011 Jon Hanna.
// This source code is licensed under the EUPL, Version 1.1 only (the “Licence”).
// You may not use, modify or distribute this work except in compliance with the Licence.
// You may obtain a copy of the Licence at:
// <http://joinup.ec.europa.eu/software/page/eupl/licence-eupl>
// A copy is also distributed with this source code.
// Unless required by applicable law or agreed to in writing, software distributed under the
// Licence is distributed on an “AS IS” basis, without warranties or conditions of any kind.

// A compiled binary is available from <http://hackcraft.github.com/Ariadne/> which if
// unmodified, may be used without restriction. (This dual-licensing is to provide a clear
// answer to the question of whether using the library in an application counts as creating
// a derivative work).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security;
using System.Security.Permissions;
using System.Threading;

namespace Ariadne.Collections
{
    /// <summary>A dictionary which is thread-safe for all operations, without locking.</summary>
    /// <remarks>The documentation of <see cref="System.Collections.Generic.IDictionary&lt;TKey, TValue>"/> states
    /// that null keys may or may not be allowed by a conformant implentation. In this case, they are.</remarks>
    /// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
    /// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
    /// <threadsafety static="true" instance="true"/>
    [Serializable]
    public sealed class LockFreeDictionary<TKey, TValue> : IDictionary<TKey, TValue>, ICloneable, ISerializable, IDictionary
    {
        private const int REPROBE_LOWER_BOUND = 5;
        private const int REPROBE_SHIFT = 5;
        private const int ZERO_HASH = 0x55555555;
        
// The dictionary is implemented as an open-addressed hash-table with linear re-probing and
// lazy deletion.
//
// Each bucket consists of a value-type (Record) that stores the hash of the key, and a
// reference type (KV) that stores the key and value.
//
// The stored hash is not just a memoised hash to avoid recomputing it, but a first-
// class part of the algorithm. Because hash values of zero are banned (any object which
// hashes to zero is given a new hash), any record with a hash of zero is guaranteed to be
// empty.
//
// Hashes once set (non-zero) are never changed. Key-values once set are only ever changed
// to a key-value with equivalent keys, or dead keys (when the key-value has been fully
// copied into a new table during an incremental resize). The leaked hashes and keys this
// results in are collected after a table resize. It is these restrictions upon the
// possible state transitions that allow safe lock-free writes from multiple threads.
// Derived classes of KV allow for detection of a lazily-deleted value, or a key-value that
// is part-way through being copied to a new table.
// 
// Possible States:
// Hash KeyValue
// 0    null    (empty record)
// h    null    (half-way through write – considered empty by read – note that unlike some,
//                  but not all, JVMs .NET CompareExchange imposes an ordering, which while it
//                  costs us performance in some cases allows us to be sure of the order of the
//                  {0, null} → {h, null} → {h → KV} transitions)
// h    KV      (h == Hash(K))
// h    KVₓ     (Tombstone KV for deleted value)
// h    KV′     (Prime KV for part-way through resize copy).
// 0    X       (Dead record that was never written to).
// h    X       (Dead record that had been written to, now copied to new table unless held
//                  tombstone).
//
// Possible Transitions:
//
// {0, null}    →   {h, null}   partway through set value.
//              →   {0, X}    dead record, use next table if you need this slot
// 
// {h, null}    →   {h, KV} where h == Hash(K)
//              →   {h, X} dead record (write never completed)
//
// {h, KV}      →   {h, KV} different value, equivalent key.
//              →   {h, KVₓ} deleted value, equivalent key, key left until resize.
//              →   {h, KV′} part-way through copying to new table.
// 
// {h, KV′}     →   {h, X} dead record
//
// The CAS-and-test mechanism that is common with lock-free mutable transitions, are used
// to ensure that only these transitions take place. E.g. after transitioning from
// {0, null} to {h, null} we test that either we succeeded or that the thread that pre-
// empted us wrote the same h we were going to write. Either way we can then safely attempt
// to try the {h, null} to {h, KV} transition. If not, we obtain another record.
//
// All the objects involved in this storage are Plain Old Data classes with no
// encapsulation (they are hidden from other assemblies entirely). This allows CASing from
// the outside code that encapsulates the transition logic.
        [Serializable]
        internal class KV
        {
            public TKey Key;
            public TValue Value;
            public KV(TKey key, TValue value)
            {
                Key = key;
                Value = value;
            }
            // Give a prime the same values as this record.
            public void FillPrime(PrimeKV prime)
            {
            	prime.Key = Key;
            	prime.Value = Value;
            }
            // a KV that is definitely not a prime
            public KV StripPrime()
            {
            	return this is PrimeKV ? new KV(Key, Value) : this;
            }
            // A dead record. Any relevant record is in the next table.
            public static readonly TombstoneKV DeadKey = new TombstoneKV(default(TKey));
            public static implicit operator KV(KeyValuePair<TKey, TValue> kvp)
            {
                return new KV(kvp.Key, kvp.Value);
            }
            public static implicit operator KeyValuePair<TKey, TValue>(KV kv)
            {
                return new KeyValuePair<TKey, TValue>(kv.Key, kv.Value);
            }
        }
        // Marks the record as part-way copied to the next table.
        internal sealed class PrimeKV : KV
        {
        	public PrimeKV()
        	    :base(default(TKey), default(TValue)){}
        }
        // There used to be a value here, but it was deleted. We can write to this
        // record again if the key is to be inserted again. Otherwise the key stays
        // stored until the next resize, when it will not be copied over.
        internal sealed class TombstoneKV : KV
        {
        	public TombstoneKV(TKey key)
        		:base(key, default(TValue)){}
        }
        private abstract class IValueMatcher
        {
            public abstract bool MatchEmpty{get;}
            public abstract bool MatchTombstone{get;}
            public abstract bool MatchValue(TValue current);
            public bool MatchKV(KV current)
            {
                if(current == null)
                    return MatchEmpty;
                if(current is TombstoneKV)
                    return MatchTombstone;
                return MatchValue(current.Value);
            }
        }
        private sealed class MatchAll : IValueMatcher
        {
            private MatchAll(){}
            public override bool MatchEmpty
            {
                get { return true; }
            }
            public override bool MatchTombstone
            {
                get { return true; }
            }
            public override bool MatchValue(TValue current)
            {
                return true;
            }
            public static readonly MatchAll Instance = new MatchAll();
        }
        private sealed class MatchEquality : IValueMatcher
        {
            private readonly IEqualityComparer<TValue> _ecmp;
            private readonly TValue _cmpVal;
            public MatchEquality(IEqualityComparer<TValue> ecmp, TValue cmpVal)
            {
                _ecmp = ecmp;
                _cmpVal = cmpVal;
            }
            public override bool MatchEmpty
            {
                get { return false; }
            }
            public override bool MatchTombstone
            {
                get { return false; }
            }
            public override bool MatchValue(TValue current)
            {
                return _ecmp.Equals(current, _cmpVal);
            }
        }
        private sealed class PredicateEquality : IValueMatcher
        {
            private readonly Func<TValue, bool> _predicate;
            private readonly bool _matchDead;
            public PredicateEquality(Func<TValue, bool> predicate, bool matchDead)
            {
                _predicate = predicate;
                _matchDead = matchDead;
            }
            public override bool MatchEmpty
            {
                get { return _matchDead; }
            }
            public override bool MatchTombstone
            {
                get { return _matchDead; }
            }
            public override bool MatchValue(TValue current)
            {
                return _predicate(current);
            }
        }
        private sealed class MatchEqualityOrDead : IValueMatcher
        {
            private readonly IEqualityComparer<TValue> _ecmp;
            private readonly TValue _cmpVal;
            public MatchEqualityOrDead(IEqualityComparer<TValue> ecmp, TValue cmpVal)
            {
                _ecmp = ecmp;
                _cmpVal = cmpVal;
            }
            public override bool MatchEmpty
            {
                get { return true; }
            }
            public override bool MatchTombstone
            {
                get { return true; }
            }
            public override bool MatchValue(TValue current)
            {
                return _ecmp.Equals(current, _cmpVal);
            }
        }
        private sealed class MatchEmptyOnly : IValueMatcher
        {
            private MatchEmptyOnly(){}
            public override bool MatchEmpty
            {
                get { return true; }
            }
            public override bool MatchTombstone
            {
                get { return false; }
            }
            public override bool MatchValue(TValue current)
            {
                return false;
            }
            public static readonly MatchEmptyOnly Instance = new MatchEmptyOnly();
        }
        private sealed class MatchDead : IValueMatcher
        {
            private MatchDead(){}
            public override bool MatchEmpty
            {
                get { return true; }
            }
            public override bool MatchTombstone
            {
                get { return true; }
            }
            public override bool MatchValue(TValue current)
            {
                return false;
            }
            public static readonly MatchDead Instance = new MatchDead();
        }
        private sealed class MatchLive : IValueMatcher
        {
            private MatchLive(){}
            public override bool MatchEmpty
            {
                get { return false; }
            }
            public override bool MatchTombstone
            {
                get { return false; }
            }
            public override bool MatchValue(TValue current)
            {
                return true;
            }
            public static readonly MatchLive Instance = new MatchLive();
        }
        // Because this is a value-type, scanning through an array of these types should play
        // nicely with CPU caches. Examinging KV requires an extra indirection into memory that
        // is less likely to be in a given level cache, but this should only rarely not give
        // us the key we are interested in (and only rarely happen at all when the key isn’t
        // present), as we don’t need to examine it at all unless we’ve found or created a
        // matching hash.
        [StructLayout(LayoutKind.Sequential, Pack=1)]
        internal struct Record
        {
            public int Hash;
            public KV KeyValue;
        }
        internal sealed class Table
        {
            public readonly Record[] Records;
            public volatile Table Next;
            public readonly Counter Size;
            public readonly Counter Slots = new Counter();
            public readonly int Capacity;
            public readonly int Mask;
            public readonly int PrevSize;
            public readonly int ReprobeLimit;
            public int CopyIdx;
            public int Resizers;
            public int CopyDone;
            public Table(int capacity, Counter size)
            {
                Records = new Record[Capacity = capacity];
                Mask = capacity - 1;
                ReprobeLimit = (capacity >> REPROBE_SHIFT) + REPROBE_LOWER_BOUND;
                if(ReprobeLimit > capacity)
                    ReprobeLimit = capacity;
                PrevSize = Size = size;
            }
        }
        
        internal Table _table;
        private readonly int _initialCapacity;
        internal readonly IEqualityComparer<TKey> _cmp;
        private const int DefaultCapacity = 1;
        private static readonly IEqualityComparer<TValue> DefaultValCmp = EqualityComparer<TValue>.Default;
        /// <summary>Constructs a new LockFreeDictionary.</summary>
        /// <param name="capacity">The initial capactiy of the dictionary</param>
        /// <param name="comparer">An <see cref="IEqualityComparer&lt;TKey>" /> that compares the keys.</param>
        public LockFreeDictionary(int capacity, IEqualityComparer<TKey> comparer)
        {
        	if(capacity < 0 || capacity > 0x4000000)
        		throw new ArgumentOutOfRangeException("capacity");
        	if(comparer == null)
        		throw new ArgumentNullException("comparer");
        	if(capacity == 0)
        		capacity = DefaultCapacity;
        	else
        	{
        	    // A classic hash-table trade-off. The (debated) advantages
        	    // of prime-number sized tables vs. the speed of masking rather
        	    // than modding. We go for the latter.
	            unchecked // binary round-up
	            {
	                --capacity;
	                capacity |= (capacity >> 1);
	                capacity |= (capacity >> 2);
	                capacity |= (capacity >> 4);
	                capacity |= (capacity >> 8);
	                capacity |= (capacity >> 16);
	                ++capacity;
	            }
        	}
            	
            _table = new Table(_initialCapacity = capacity, new Counter());
            _cmp = comparer;
        }
        /// <summary>Constructs a new LockFreeDictionary.</summary>
        /// <param name="capacity">The initial capactiy of the dictionary</param>
        public LockFreeDictionary(int capacity)
            :this(capacity, EqualityComparer<TKey>.Default){}
        /// <summary>Constructs a new LockFreeDictionary.</summary>
        /// <param name="comparer">An <see cref="IEqualityComparer&lt;TKey>" /> that compares the keys.</param>
        public LockFreeDictionary(IEqualityComparer<TKey> comparer)
            :this(DefaultCapacity, comparer){}
        /// <summary>Constructs a new LockFreeDictionary.</summary>
        public LockFreeDictionary()
            :this(DefaultCapacity){}
        private static int EstimateNecessaryCapacity(IEnumerable<KeyValuePair<TKey, TValue>> collection)
        {
            // we want to avoid pointless re-sizes if we can tell what size the source collection is, (though in
            // worse cases it could be a million items!), but can only do so for some collection types.
        	if(collection == null)
        	    throw new ArgumentNullException("collection", Strings.Dict_Null_Source_Collection);
        	try
        	{
            	ICollection<KeyValuePair<TKey, TValue>> colKVP = collection as ICollection<KeyValuePair<TKey, TValue>>;
            	if(colKVP != null)
            	    return Math.Min(colKVP.Count, 1024); // let’s not go above 1024 just in case there’s only a few distinct items.
            	ICollection col = collection as ICollection;
            	if(col != null)
            	    return Math.Min(col.Count, 1024);
        	}
        	catch
        	{
        	    // if some collection throws on Count but doesn’t throw when iterated through, then well that would be
        	    // pretty weird, but since our calling Count is an optimisation, we should tolerate that.
        	}
        	return DefaultCapacity;
        }
        /// <summary>Constructs a new LockFreeDictionary.</summary>
        /// <param name="collection">A collection from which the dictionary will be filled.</param>
        /// <param name="comparer">An <see cref="IEqualityComparer&lt;TKey>" /> that compares the keys.</param>
        public LockFreeDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey> comparer)
        	:this(EstimateNecessaryCapacity(collection), comparer)
        {
        	foreach(KeyValuePair<TKey, TValue> kvp in collection)
        		this[kvp.Key] = kvp.Value;
        }
        /// <summary>Constructs a new LockFreeDictionary.</summary>
        /// <param name="collection">A collection from which the dictionary will be filled.</param>
        public LockFreeDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection)
            :this(collection, EqualityComparer<TKey>.Default){}
        private static Func<TKey, TValue, bool, TValue> ProducerFromFactory(Func<TKey, TValue> factory)
        {
            bool called = false;
            TValue cached = default(TValue);
            return (TKey key, TValue value, bool factoryMode) =>
            {
                if(factoryMode)
                {
                    if(!called)
                    {
                        cached = factory(key);
                        called = true;
                    }
                    return cached;
                }
                return value;
            };
        }
        private static Func<TKey, TValue, bool, TValue> ProducerFromUpdaterAndValue(Func<TKey, TValue, TValue> updater, TValue value)
        {
            return (TKey key, TValue curValue, bool factoryMode) =>
            {
                if(factoryMode)
                    return value;
                return updater(key, curValue);
            };
        }
        private static Func<TKey, TValue, bool, TValue> ProducerFromUpdaterAndFactory(Func<TKey, TValue, TValue> updater, Func<TKey, TValue> factory)
        {
            bool factoryCalled = false;
            TValue cachedFromFactory = default(TValue);
            return (TKey key, TValue value, bool factoryMode) =>
            {
                if(factoryMode)
                {
                    if(!factoryCalled)
                    {
                        cachedFromFactory = factory(key);
                        factoryCalled  = true;
                    }
                    return cachedFromFactory;
                }
                return updater(key, value);
            };
        }
        internal int Hash(TKey key)
        {
            //We must prohibit the value of zero in order to be sure that when we encounter a
            //zero, that the hash has not been written.
            //We do not use a Wang-Jenkins like Dr. Click’s approach, since .NET’s IComparer allows
            //users of the class to fix the effects of poor hash algorithms. Let’s not penalise great
            //hashes with more work and potentially even make good hashes less good.
            int givenHash = _cmp.GetHashCode(key);
            return givenHash == 0 ? ZERO_HASH : givenHash;
        }
        [SecurityCritical] 
        [SecurityPermission(SecurityAction.Demand, SerializationFormatter=true)]
        void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("ic", _initialCapacity);
            info.AddValue("cmp", _cmp, typeof(IEqualityComparer<TKey>));
            List<KV> list = new List<KV>(Count);
            foreach(KV pair in EnumerateKVs())
                list.Add(pair);
            KV[] arr = list.ToArray();
            info.AddValue("arr", arr);
            info.AddValue("cKVP", arr.Length);
        }
        private LockFreeDictionary(SerializationInfo info, StreamingContext context)
            :this(info.GetInt32("cKVP"), (IEqualityComparer<TKey>)info.GetValue("cmp", typeof(IEqualityComparer<TKey>)))
        {
            _initialCapacity = info.GetInt32("ic");
            KV[] arr = (KV[])info.GetValue("arr", typeof(KV[]));
            for(int i = 0; i != arr.Length; ++i)
                this[arr[i].Key] = arr[i].Value;
        }
        // Try to get a value from a table. If necessary, move to the next table. 
        private bool Obtain(Table table, TKey key, int hash, out TValue value)
        {
            for(;;)
            {
                int idx = hash & table.Mask;
                int reprobeCount = 0;
                int maxProbe = table.ReprobeLimit;
                Record[] records = table.Records;
                for(;;)
                {
                    int curHash = records[idx].Hash;
                    if(curHash == 0)//nothing written to this record
                    {
                        Table next = table.Next;
                        if(next != null)
                        {
                            table = next;
                            break;
                        }
                        value = default(TValue);
                        return false;
                    }
                    KV pair = records[idx].KeyValue;
                    if(curHash == hash)//hash we care about, is it the key we care about?
                    {
                        if(_cmp.Equals(key, pair.Key))//key’s match, and this can’t change.
                        {
                        	PrimeKV asPrime = pair as PrimeKV;
                            if(asPrime != null)
                            {
                                // A resize-copy is part-way through. Let’s make sure it completes before hitting that
                                // other table (we do this rather than trust the value we found, because the next table
                                // could have a more recently-written value).
                            	CopySlotsAndCheck(table, asPrime, idx);
                            	// Note that Click has reading operations help the resize operation.
                            	// Avoiding it here is not so much to optimise for the read operation’s speed (though it will)
                                // as it is to reduce the chances of a purely read-only operation throwing an OutOfMemoryException
                            	// in heavily memory-constrained scenarios. Since reading wouldn’t conceptually add to
                            	// memory use, this could be surprising to users.
                                table = table.Next;
                                break;
                            }
                            else if(pair is TombstoneKV)
                            {
                                value = default(TValue);
                                return false;
                            }
                            else
                            {
                                value = pair.Value;
                                return true;
                            }
                        }
                    }
                    if(++reprobeCount >= maxProbe)
                    {
                        //if there’s a resize in progress, the desired value might be in the next table.
                        //otherwise, it doesn’t exist.
                        Table next = table.Next;
                        if(next == null)
                        {
                            value = default(TValue);
                            return false;
                        }
                        table = next;
                        break;
                    }
                    idx = (idx + 1) & table.Mask;
                }
            }
        }
        // try to put a value into the dictionary, as long as the current value
        // matches oldPair based on the dictionary’s key-comparison rules
        // and the value-comparison rule passed through.
        private bool PutIfMatch(KV pair, IValueMatcher valCmp, Func<TKey, TValue, bool, TValue> producer, out KV replaced)
        {
            return PutIfMatch(_table, pair, Hash(pair.Key), valCmp, producer, out replaced);
        }
        private bool PutIfMatch(KV pair, IValueMatcher valCmp, Func<TKey, TValue, bool, TValue> producer)
        {
            KV dontCare;
            return PutIfMatch(pair, valCmp, producer, out dontCare);
        }
        private bool PutIfMatch(KV pair, IValueMatcher valCmp)
        {
            KV dontCare;
            return PutIfMatch(pair, valCmp, null, out dontCare);
        }
        // try to put a value into a table. If there is a resize in progress
        // we mark the relevant slot in this table if necessary before writing
        // to the next table.
        private bool PutIfMatch(Table table, KV pair, int hash, IValueMatcher valCmp, Func<TKey, TValue, bool, TValue> producer, out KV replaced)
        {
            //Restart with next table by goto-ing to this label. Just as flame-bait for people quoting
            //Dijkstra (well, that and it avoids recursion with a measurable performance improvement -
            //essentially this is doing tail-call optimisation by hand, the compiler could do this, but
            //measurements suggest it doesn’t, or we wouldn’t witness any speed increase).
        restart:
            int mask = table.Mask;
            int idx = hash & mask;
            int reprobeCount = 0;
            int maxProbe = table.ReprobeLimit;
            Record[] records = table.Records;
            KV curPair = null;
            for(;;)
            {
                int curHash = records[idx].Hash;
                if(curHash == 0)//nothing written here
                {
                    if(pair is TombstoneKV || !valCmp.MatchEmpty)
                    {
                        replaced = null;//don’t change anything.
                        return false;
                    }
                    if((curHash = Interlocked.CompareExchange(ref records[idx].Hash, hash, 0)) == 0)
                        curHash = hash;
                    //now fallthrough to the next check, which we will pass if the above worked
                    //or if another thread happened to write the same hash we wanted to write
                    //(hence our doing curHash = hash in the failure case)
                }
                if(curHash == hash)
                {
                    //hashes match, do keys?
                    //while retrieving the current
                    //if we want to write to empty records
                    //let’s see if we can just write because there’s nothing there...
                    if(valCmp.MatchEmpty)
                    {
                        if(producer != null && records[idx].KeyValue == null)
                            pair.Value = producer(pair.Key, default(TValue), true);
                        if((curPair = Interlocked.CompareExchange(ref records[idx].KeyValue, pair, null)) == null)
                        {
                            table.Slots.Increment();
                            if(!(valCmp is MatchEmptyOnly))//MatchEmptyOnly only used on resize, others never used on resize.
                                table.Size.Increment();
                            replaced = null;
                            return true;
                        }
                    }
                    else
                        curPair = records[idx].KeyValue;
                    //okay there’s something with the same hash here, does it have the same key?
                    if(_cmp.Equals(curPair.Key, pair.Key))
                        break;
                }
                else
                    curPair = records[idx].KeyValue; //just to check for dead records
                if(curPair == KV.DeadKey || ++reprobeCount >= maxProbe)
                {
                    Table next = table.Next ?? Resize(table);
                    //test if we’re putting from a copy
                    //and don’t do this if that’s
                    //the case
                    HelpCopy(table, new PrimeKV(), false);
                    table = next;
                    goto restart;
                }
                idx = (idx + 1) & mask;
            }
            //we have a record with a matching key.

            if(valCmp is MatchEmptyOnly)
            {
                replaced = curPair;
                return false;
            }
            if(curPair is TombstoneKV)
            {
                if(!valCmp.MatchTombstone)//short-cut on tombstone
                {
                    replaced = curPair;
                    return false;
                }
            }
            else if(producer == null)
            {
                //(If the type of TValue is a value type, then the call to reference equals can only ever return false
                //(after an expensive box), and is pretty much meaningless. Hopefully the jitter would have done a nice job of either
                //removing the IsValueType checks and leaving the ReferenceEquals call or or removing the entire thing).
                if(!typeof(TValue).IsValueType && ReferenceEquals(pair.Value, curPair.Value))//short-cut on no change
                {
                    replaced = curPair;
                    return valCmp.MatchValue(curPair.Value);
                }
            }
            
            //If there’s a resize in progress then we want to ultimately write to the final table. First we make sure that the
            //current record is copied over - so that any reader won’t end up finding the previous value and not realise it
            //should look to the next table - and then we write there.
            if(table.Next != null)
            {
                PrimeKV prime = new PrimeKV();
                CopySlotsAndCheck(table, prime, idx);
                HelpCopy(table, prime, false);
                table = table.Next;
                goto restart;
            }
            
            for(;;)
            {
                //if we don’t match the conditions in which we want to overwrite a value
                //then we just return the current value, and change nothing.
                if(!valCmp.MatchKV(curPair))
                {
                    replaced = curPair;
                    return false;
                }
                
                if(producer != null)
                {
                    bool factoryMode = curPair == null || curPair is TombstoneKV;
                    pair.Value = producer(pair.Key, factoryMode ? default(TValue) : curPair.Value, factoryMode);
                }
                
                KV prevPair = Interlocked.CompareExchange(ref records[idx].KeyValue, pair, curPair);
                if(prevPair == curPair)
                {
                    if(pair is TombstoneKV)
                    {
                    	if(!(prevPair is TombstoneKV))
                            table.Size.Decrement();
                    }
                    else if(prevPair is TombstoneKV)
                        table.Size.Increment();
                    replaced = prevPair;
                    return true;
                }
                
                //we lost the race, another thread set the pair.
                PrimeKV prevPrime = prevPair as PrimeKV;
                if(prevPrime != null)
                {
                    CopySlotsAndCheck(table, prevPrime, idx);
                    HelpCopy(table, prevPrime, false);
                    table = table.Next;
                    goto restart;
                }
                else if(prevPair == KV.DeadKey)
                {
                    table = table.Next;
                    goto restart;
                }
                else
                    curPair = prevPair;
            }
        }
        // Copies a record to the next table, and checks if there should be a promotion.
        internal void CopySlotsAndCheck(Table table, PrimeKV prime, int idx)
        {
            if(CopySlot(table, prime, idx))
                CopySlotAndPromote(table, 1);
        }
        // Copy a bunch of records to the next table.
        private void HelpCopy(Table table, PrimeKV prime, bool all)
        {
            //Some things to note about our maximum chunk size. First, it’s a nice round number which will probably
            //result in a bunch of complete cache-lines being dealt with. It’s also big enough number that we’re not
            //at risk of false-sharing with another thread (that is, where two resizing threads keep causing each other’s
            //cache-lines to be invalidated with each write.
            int chunk = table.Capacity;
            if(chunk > 1024)
                chunk = 1024;
            while(table.CopyDone < table.Capacity)
            {
                int copyIdx = Interlocked.Add(ref table.CopyIdx, chunk) & table.Mask;
                int workDone = 0;
                for(int i = 0; i != chunk; ++i)
                    if(CopySlot(table, prime, copyIdx + i))
                        ++workDone;
                if(workDone != 0)
                    CopySlotAndPromote(table, workDone);
                if(!all)
                    return;
            }
        }
        //If we’ve just finished copying the current table, then we should make the next table
        //the current table, let the old one be collected, and then check to make sure that
        //it isn’t also copied over.
        private void CopySlotAndPromote(Table table, int workDone)
        {
            if(Interlocked.Add(ref table.CopyDone, workDone) >= table.Capacity && table == _table)
                while(Interlocked.CompareExchange(ref _table, table.Next, table) == table)
                {
                    table = _table;
                    if(table.CopyDone < table.Capacity)
                        break;
                }
        }
        private bool CopySlot(Table table, PrimeKV prime, int idx)
        {
            //Note that the prime is passed through. This prevents the allocation of multiple
            //objects which will in the normal case become unnecessary after this method completes
            //and in the case of an exception would be abandoned by the thread anyway.
            //This is pretty much a pure optimisation, but does profile as giving real improvements.
            //There could be a temptation to go further and have a single permanent prime in tread-local
            //storage. However, this fails in the following case:
            //1. Thread is aborted or otherwise rudely interrupted after putting the prime in the table
            //but before over-writing it with the dead record.
            //2. This exception is caught, and the thread is put back into service.
            //3. The thread uses its thread-local prime in another operation, hence mutating the value
            //written in step 1 above (perhaps in a different table in a different dictionary).
            //Besides which, such thread-local implementation could profile as giving a tiny improvement in tests
            //and then hammer memory usage in real world use!

            Record[] records = table.Records;
            //if unwritten-to we should be able to just mark it as dead.
            KV kv = Interlocked.CompareExchange(ref records[idx].KeyValue, KV.DeadKey, null);
            if(kv  == null)
                return true;
            KV oldKV = kv;
            while(!(kv is PrimeKV))
            {
            	if(kv is TombstoneKV)
            	{
            		oldKV = Interlocked.CompareExchange(ref records[idx].KeyValue, KV.DeadKey, kv);
            		if(oldKV == kv)
            			return true;
            	}
            	else
            	{
	            	kv.FillPrime(prime);
	                oldKV = Interlocked.CompareExchange(ref records[idx].KeyValue, prime, kv);
	                if(kv == oldKV)
	                {
	                    if(kv is TombstoneKV)
	                        return true;
	                    kv = prime;
	                    break;
	                }
            	}
                kv = oldKV;
            }
            if(kv is TombstoneKV)
                return false;

            KV newRecord = oldKV.StripPrime();
            
            KV dontCare;
            bool copied = PutIfMatch(table.Next, newRecord, records[idx].Hash, MatchEmptyOnly.Instance, null, out dontCare);
            
            while((oldKV = Interlocked.CompareExchange(ref records[idx].KeyValue, KV.DeadKey, kv)) != kv)
                kv = oldKV;
            
            return copied;
        }
        private static Table Resize(Table tab)
        {
            //Heuristic is a polite word for guesswork! Almost certainly the heuristic here could be improved,
            //but determining just how best to do so requires the consideration of many different cases.
            int sz = tab.Size;
            int cap = tab.Capacity;
            Table next = tab.Next;
            if(next != null)
                return next;
            int newCap;
            if(sz >= cap * 3 / 4)
                newCap = sz * 8;
            else if(sz >= cap / 2)
                newCap = sz * 4;
            else if(sz >= cap / 4)
                newCap = sz * 2;
            else
                newCap = sz;
         	if(tab.Slots >= sz << 1)
                newCap = cap << 1;
            if(newCap < cap)
                newCap = cap;
            if(sz == tab.PrevSize)
                newCap *= 2;

            unchecked // binary round-up
            {
                --newCap;
                newCap |= (newCap >> 1);
                newCap |= (newCap >> 2);
                newCap |= (newCap >> 4);
                newCap |= (newCap >> 8);
                newCap |= (newCap >> 16);
                ++newCap;
            }
            
            int resizers = Interlocked.Increment(ref tab.Resizers);
            int MB = newCap / 0x40000;
            if(MB > 0 && resizers > 2)
            {
                if((next = tab.Next) != null)
                    return next;
                Thread.SpinWait(20);
                if((next = tab.Next) != null)
                    return next;
                Thread.Sleep(Math.Max(MB * 5 * resizers, 200));
            }
            
            if((next = tab.Next) != null)
                return next;
            
            next = new Table(newCap, tab.Size);

            #pragma warning disable 420 // CompareExchange has its own volatility guarantees
            return Interlocked.CompareExchange(ref tab.Next, next, null) ?? next;
			#pragma warning restore 420
        }
        /// <summary>The current capacity of the dictionary.</summary>
        /// <remarks>If the dictionary is in the midst of a resize, the capacity it is resizing to is returned, ignoring other internal storage in use.</remarks>
        public int Capacity
        {
        	get
        	{
        		return _table.Capacity;
        	}
        }
        /// <summary>Creates an <see cref="System.Collections.Generic.IDictionary&lt;TKey, TValue>"/> that is
        /// a copy of the current contents.</summary>
        /// <remarks>Because this operation does not lock, the resulting dictionary’s contents
        /// could be inconsistent in terms of an application’s use of the values.
        /// <para>If there is a value stored with a null key, it is ignored.</para></remarks>
        /// <returns>The <see cref="System.Collections.Generic.IDictionary&lt;TKey, TValue>"/>.</returns>
    	public Dictionary<TKey, TValue> ToDictionary()
    	{
    		Dictionary<TKey, TValue> snapshot = new Dictionary<TKey, TValue>(Count, _cmp);
    		foreach(KV kv in EnumerateKVs())
    			if(kv.Key != null)
    				snapshot[kv.Key] = kv.Value;
    		return snapshot;
    	}
    	object ICloneable.Clone()
    	{
    	    return Clone();
    	}
    	/// <summary>Returns a copy of the current dictionary.</summary>
        /// <remarks>Because this operation does not lock, the resulting dictionary’s contents
        /// could be inconsistent in terms of an application’s use of the values.</remarks>
        /// <returns>The <see cref="LockFreeDictionary&lt;TKey, TValue>"/>.</returns>
        public LockFreeDictionary<TKey, TValue> Clone()
        {
        	LockFreeDictionary<TKey, TValue> snapshot = new LockFreeDictionary<TKey, TValue>(Count, _cmp);
        	foreach(KV kv in EnumerateKVs())
        		snapshot.PutIfMatch(kv, MatchAll.Instance);
        	return snapshot;
        }
        /// <summary>Gets or sets the value for a particular key.</summary>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">The key was not present in the dictionary.</exception>
        public TValue this[TKey key]
        {
            get
            {
                TValue ret;
                if(TryGetValue(key, out ret))
                    return ret;
                throw new KeyNotFoundException(key.ToString());
            }
            set
            {
                PutIfMatch(new KV(key, value), MatchAll.Instance);
            }
        }
        /// <summary>Returns the collection of keys in the system.</summary>
        /// <remarks>This is a live collection, which changes with changes to the dictionary.</remarks>
        public KeyCollection Keys
        {
            get { return new KeyCollection(this); }
        }
        ICollection<TKey> IDictionary<TKey, TValue>.Keys
        {
        	get { return Keys; }
        }
        /// <summary>Returns the collection of values in the system.</summary>
        /// <remarks>This is a live collection, which changes with changes to the dictionary.</remarks>
        public ValueCollection Values
        {
        	get { return new ValueCollection(this); }
        }
        ICollection<TValue> IDictionary<TKey, TValue>.Values
        {
        	get { return Values; }
        }
        /// <summary>Returns an estimate of the current number of items in the dictionary.</summary>
        public int Count
        {
            get { return _table.Size; }
        }
        
        bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly
        {
            get { return false; }
        }
        bool IDictionary.IsReadOnly
        {
            get { return false; }
        }
        /// <summary>Tests whether a given key is present in the collection.</summary>
        /// <param name="key">The key to search for.</param>
        /// <returns>True if the key is present, false otherwise.</returns>
        public bool ContainsKey(TKey key)
        {
            TValue dummy;
            return TryGetValue(key, out dummy);
        }
        /// <summary>Adds a key and value to the collection, as long as it is not currently present.</summary>
        /// <param name="key">The key to add.</param>
        /// <param name="value">The value to add.</param>
        /// <exception cref="System.ArgumentException">An item with the same key has already been added.</exception>
        public void Add(TKey key, TValue value)
        {
            if(!TryAdd(key, value))
                throw new ArgumentException(Strings.Dict_Same_Key, "key");
        }
        /// <summary>Attempts to add the specified key and value into the dictionary.</summary>
        /// <param name="key">The key to add.</param>
        /// <param name="value">The value to add.</param>
        /// <param name="existing">The existing value for the key, if the attempt fails. The default value of <c>TValue</c> if it succeeds.</param>
        /// <returns>True if the method succeeds, false if there was a value for the given key.</returns>
        public bool TryAdd(TKey key, TValue value, out TValue existing)
        {
            KV prev;
            if(PutIfMatch(new KV(key, value), MatchDead.Instance, null, out prev))
            {
                existing = default(TValue);
                return true;
            }
            existing = prev.Value;
            return false;
        }
        /// <summary>Attempts to add the specified key and value into the dictionary.</summary>
        /// <param name="key">The key to add.</param>
        /// <param name="value">The value to add.</param>
        /// <returns>True if the method succeeds, false if there was a value for the given key.</returns>
        public bool TryAdd(TKey key, TValue value)
        {
            TValue dontCare;
            return TryAdd(key, value, out dontCare);
        }
        /// <summary>Attempts to add the specified key and value into the dictionary.</summary>
        /// <param name="kvp">The key and value to add.</param>
        /// <returns>True if the method succeeds, false if there was a value for the given key.</returns>
        public bool TryAdd(KeyValuePair<TKey, TValue> kvp)
        {
            return PutIfMatch(kvp, MatchDead.Instance);
        }
        /// <summary>Attempts to add a key and value to the dictionary, producing the value as needed.</summary>
        /// <param name="key">The key to add.</param>
        /// <param name="factory">The function to produce the value for the absent key.</param>
        /// <param name="existing">The existing value for the key, if the attempt fails. The default value of <c>TValue</c> if it succeeds.</param>
        /// <returns>True if the method succeeds, false if there was a value for the given key.</returns>
        /// <remarks><para><paramref name="factory"/> will only be invoked if the key is not already present in the dictionary. However, it will not block other
        /// attempts to add values for the key, and so the method can still fail after it has been called.</para>
        /// <para>It is up to the caller to ensure that
        /// <paramref name="factory"/> has an appropriate degree of thread-safety.</para></remarks>
        public bool TryAdd(TKey key, Func<TKey, TValue> factory, out TValue existing)
        {
            if(factory == null)
                throw new ArgumentNullException("factory");
            KV prev;
            if(PutIfMatch(new KV(key, default(TValue)), MatchDead.Instance, ProducerFromFactory(factory), out prev))
            {
                existing = default(TValue);
                return true;
            }
            existing = prev.Value;
            return false;
        }
        /// <summary>Attempts to add a key and value to the dictionary, producing the value as needed.</summary>
        /// <param name="key">The key to add.</param>
        /// <param name="factory">The function to produce the value for the absent key.</param>
        /// <returns>True if the method succeeds, false if there was a value for the given key.</returns>
        /// <remarks><para><paramref name="factory"/> will only be invoked if the key is not already present in the dictionary. However, it will not block other
        /// attempts to add values for the key, and so the method can still fail after it has been called.</para>
        /// <para>It is up to the caller to ensure that
        /// <paramref name="factory"/> has an appropriate degree of thread-safety.</para></remarks>
        public bool TryAdd(TKey key, Func<TKey, TValue> factory)
        {
            TValue dontCare;
            return TryAdd(key, factory, out dontCare);
        }
        /// <summary>Updates the value for a key if it exists and if the current value matches one given.</summary>
        /// <param name="key">The key to update.</param>
        /// <param name="value">The new value to set for the key.</param>
        /// <param name="compare">The old value that must be present for the key.</param>
        /// <param name="comparer">An <see cref="IEqualityComparer&lt;T>"/> used in comparing <paramref name="compare"/> with the current value.</param>
        /// <param name="previous">The previous value if there was one for the key, irrespective of whether the method succeeds or not, or the default
        /// value for <c>TValue</c> if there was no such key present.</param>
        /// <returns>True if the key was updated, false if it wasn’t present or if <paramref name="compare"/> did not match the current value.</returns>
        public bool Update(TKey key, TValue value, TValue compare, IEqualityComparer<TValue> comparer, out TValue previous)
        {
            if(comparer == null)
                throw new ArgumentNullException("comparer");
            KV old;
            if(PutIfMatch(new KV(key, value), new MatchEquality(comparer, compare), null, out old))
            {
                previous = old.Value;
                return true;
            }
            previous = old == null ? default(TValue) : old.Value;
            return false;
        }
        /// <summary>Updates the value for a key if it exists and if the current value matches one given.</summary>
        /// <param name="key">The key to update.</param>
        /// <param name="value">The new value to set for the key.</param>
        /// <param name="compare">The old value that must be present for the key.</param>
        /// <param name="comparer">An <see cref="IEqualityComparer&lt;T>"/> used in comparing <paramref name="compare"/> with the current value.</param>
        /// <returns>True if the key was updated, false if it wasn’t present or if <paramref name="compare"/> did not match the current value.</returns>
        public bool Update(TKey key, TValue value, TValue compare, IEqualityComparer<TValue> comparer)
        {
            TValue dontCare;
            return Update(key, value, compare, comparer, out dontCare);
        }
        /// <summary>Updates the value for a key if it exists and if the current value matches one given.</summary>
        /// <param name="key">The key to update.</param>
        /// <param name="value">The new value to set for the key.</param>
        /// <param name="compare">The old value that must be present for the key.</param>
        /// <param name="previous">The previous value if there was one for the key, irrespective of whether the method succeeds or not, or the default
        /// value for <c>TValue</c> if there was no such key present.</param>
        /// <returns>True if the key was updated, false if it wasn’t present or if <paramref name="compare"/> did not match the current value.</returns>
        public bool Update(TKey key, TValue value, TValue compare, out TValue previous)
        {
            return Update(key, value, compare, DefaultValCmp, out previous);
        }
        /// <summary>Updates the value for a key if it exists and if the current value matches one given.</summary>
        /// <param name="key">The key to update.</param>
        /// <param name="value">The new value to set for the key.</param>
        /// <param name="compare">The old value that must be present for the key.</param>
        /// <returns>True if the key was updated, false if it wasn’t present or if <paramref name="compare"/> did not match the current value.</returns>
        public bool Update(TKey key, TValue value, TValue compare)
        {
            TValue dontCare;
            return Update(key, value, compare, out dontCare);
        }
        /// <summary>Updates the value for a key if it exists and if the current value matches one given.</summary>
        /// <param name="key">The key to update.</param>
        /// <param name="value">The new value to set for the key.</param>
        /// <param name="predicate">A <see cref="Func&lt;T, TResult>"/> that returns true for values that should be replaced, false for those that
        /// should remain.</param>
        /// <param name="previous">The previous value if there was one for the key, irrespective of whether the method succeeds or not, or the default
        /// value for <c>TValue</c> if there was no such key present.</param>
        /// <returns>True if the key was updated, false if it wasn’t present or if <paramref name="predicate"/> returned false.</returns>
        /// <remarks>While the update will be atomic when (and if) it happens, it is possible for another thread to update or delete the value
        /// in the meantime. Therefore predicate may be called more than once, until either the operation succeeds, another thread
        /// deletes the value, or the predicate returns false.</remarks>
        public bool Update(TKey key, TValue value, Func<TValue, bool> predicate, out TValue previous)
        {
            if(predicate == null)
                throw new ArgumentNullException("predicate");
            KV old;
            if(PutIfMatch(new KV(key, value), new PredicateEquality(predicate, false), null, out old))
            {
                previous = old.Value;
                return true;
            }
            previous = old == null ? default(TValue) : old.Value;
            return false;
        }
        /// <summary>Updates the value for a key if it exists and if the current value matches one given.</summary>
        /// <param name="key">The key to update.</param>
        /// <param name="value">The new value to set for the key.</param>
        /// <param name="predicate">A <see cref="Func&lt;T, TResult>"/> that returns true for values that should be replaced, false for those that
        /// should remain.</param>
        /// <returns>True if the key was updated, false if it wasn’t present or if <paramref name="predicate"/> returned false.</returns>
        /// <remarks>While the update will be atomic when (and if) it happens, it is possible for another thread to update or delete the value
        /// in the meantime. Therefore predicate may be called more than once, until either the operation succeeds, another thread
        /// deletes the value, or the predicate returns false.</remarks>
        public bool Update(TKey key, TValue value, Func<TValue, bool> predicate)
        {
            TValue dontCare;
            return Update(key, value, predicate, out dontCare);
        }
        /// <summary>Adds a value for a key, or updates it if the current value matches one given.</summary>
        /// <param name="key">The key to add or change the value for.</param>
        /// <param name="value">The value to add.</param>
        /// <param name="compare">The value to compare with the current value, if present.</param>
        /// <param name="comparer">An <see cref="IEqualityComparer&lt;T>"/> to use to compare <paramref name="compare"/> with the current value.</param>
        /// <param name="previous">The previous value, if there was one, or the default value for <c>TValue</c> otherwise.</param>
        /// <returns>True if the value was set (added or updated), false otherwise.</returns>
        public bool AddOrUpdate(TKey key, TValue value, TValue compare, IEqualityComparer<TValue> comparer, out TValue previous)
        {
            KV old;
            bool ret = PutIfMatch(new KV(key, value), new MatchEqualityOrDead(comparer, compare), null, out old);
            previous = old == null ? default(TValue) : old.Value;
            return ret;
        }
        /// <summary>Adds a value for a key, or updates it if the current value matches one given.</summary>
        /// <param name="key">The key to add or change the value for.</param>
        /// <param name="value">The value to add.</param>
        /// <param name="compare">The value to compare with the current value, if present.</param>
        /// <param name="comparer">An <see cref="IEqualityComparer&lt;T>"/> to use to compare <paramref name="compare"/> with the current value.</param>
        /// <returns>True if the value was set (added or updated), false otherwise.</returns>
        public bool AddOrUpdate(TKey key, TValue value, TValue compare, IEqualityComparer<TValue> comparer)
        {
            TValue dontCare;
            return AddOrUpdate(key, value, compare, comparer, out dontCare);
        }
        /// <summary>Adds a value for a key, or updates it if the current value matches one given.</summary>
        /// <param name="key">The key to add or change the value for.</param>
        /// <param name="value">The value to add.</param>
        /// <param name="compare">The value to compare with the current value, if present.</param>
        /// <param name="previous">The previous value, if there was one, or the default value for <c>TValue</c> otherwise.</param>
        /// <returns>True if the value was set (added or updated), false otherwise.</returns>
        public bool AddOrUpdate(TKey key, TValue value, TValue compare, out TValue previous)
        {
            return AddOrUpdate(key, value, compare, DefaultValCmp, out previous);
        }
        /// <summary>Adds a value for a key, or updates it if the current value matches one given.</summary>
        /// <param name="key">The key to add or change the value for.</param>
        /// <param name="value">The value to add.</param>
        /// <param name="compare">The value to compare with the current value, if present.</param>
        /// <returns>True if the value was set (added or updated), false otherwise.</returns>
        public bool AddOrUpdate(TKey key, TValue value, TValue compare)
        {
            TValue dontCare;
            return AddOrUpdate(key, value, compare, out dontCare);
        }
        /// <summary>Adds a value for a key, or updates it if the current value matches one given.</summary>
        /// <param name="key">The key to add or change the value for.</param>
        /// <param name="addValue">The value to add, if none is present.</param>
        /// <param name="updater">A <see cref="Func&lt;T1, T2, TResult>"/> that will produce a value from the key and current value
        /// to update the current value.</param>
        /// <param name="previous">The previous value, if there was one, or the default value for <c>TValue</c> otherwise.</param>
        /// <returns>The new value.</returns>
        /// <remarks>While the addition or update will be atomic when it happens, and <paramref name="updater"/> will only be
        /// called if there is a value present, it will not block other threads from changing or removing the value while it
        /// is running. It is therefore possible for it to be called multiple times, or for <paramref name="addValue"/> to
        /// be used after it was called.</remarks>
        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updater, out TValue previous)
        {
            KV pair = new KV(key, addValue);
            KV old;
            PutIfMatch(pair, MatchAll.Instance, ProducerFromUpdaterAndValue(updater, addValue), out old);
            previous = old == null ? default(TValue) : old.Value;
            return pair.Value;
        }
        /// <summary>Adds a value for a key, or updates it if the current value matches one given.</summary>
        /// <param name="key">The key to add or change the value for.</param>
        /// <param name="addValue">The value to add, if none is present.</param>
        /// <param name="updater">A <see cref="Func&lt;T1, T2, TResult>"/> that will produce a value from the key and current value
        /// to update the current value.</param>
        /// <returns>The new value.</returns>
        /// <remarks>While the addition or update will be atomic when it happens, and <paramref name="updater"/> will only be
        /// called if there is a value present, it will not block other threads from changing or removing the value while it
        /// is running. It is therefore possible for it to be called multiple times, or for <paramref name="addValue"/> to
        /// be used after it was called.</remarks>
        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updater)
        {
            TValue dontCare;
            return AddOrUpdate(key, addValue, updater, out dontCare);
        }
        /// <summary>Adds a value for a key, or updates it if the current value matches one given.</summary>
        /// <param name="key">The key to add or change the value for.</param>
        /// <param name="factory">A <see cref="Func&lt;T, TResult>"/> that will produce a value to add to the dictionary.</param>
        /// <param name="updater">A <see cref="Func&lt;T1, T2, TResult>"/> that will produce a value from the key and current value
        /// to update the current value.</param>
        /// <param name="previous">The previous value, if there was one, or the default value for <c>TValue</c> otherwise.</param>
        /// <returns>The new value.</returns>
        /// <remarks>While the addition or update will be atomic when it happens, <paramref name="factory"/> will be called only
        /// once and <paramref name="updater"/> will only be
        /// called if there is a value present, <paramref name="factory"/> and <paramref name="updater"/>
        /// will not block other threads from changing or removing the value while they are running
        /// is running. It is therefore possible for <paramref name="updater"/> to be called multiple times,
        /// for both <paramref name="factory"/> and <paramref name="updater"/> to be called, or for the result
        /// of <paramref name="factory"/> to be used after <paramref name="updater"/> was called.</remarks>
        public TValue AddOrUpdate(TKey key, Func<TKey, TValue> factory, Func<TKey, TValue, TValue> updater, out TValue previous)
        {
            KV pair = new KV(key, default(TValue));
            KV old;
            PutIfMatch(pair, MatchAll.Instance, ProducerFromUpdaterAndFactory(updater, factory), out old);
            previous = old == null ? default(TValue) : old.Value;
            return pair.Value;
        }
        /// <summary>Adds a value for a key, or updates it if the current value matches one given.</summary>
        /// <param name="key">The key to add or change the value for.</param>
        /// <param name="factory">A <see cref="Func&lt;T, TResult>"/> that will produce a value to add to the dictionary.</param>
        /// <param name="updater">A <see cref="Func&lt;T1, T2, TResult>"/> that will produce a value from the key and current value
        /// to update the current value.</param>
        /// <returns>The new value.</returns>
        /// <remarks>While the addition or update will be atomic when it happens, <paramref name="factory"/> will be called only
        /// once and <paramref name="updater"/> will only be
        /// called if there is a value present, <paramref name="factory"/> and <paramref name="updater"/>
        /// will not block other threads from changing or removing the value while they are running
        /// is running. It is therefore possible for <paramref name="updater"/> to be called multiple times,
        /// for both <paramref name="factory"/> and <paramref name="updater"/> to be called, or for the result
        /// of <paramref name="factory"/> to be used after <paramref name="updater"/> was called.</remarks>
        public TValue AddOrUpdate(TKey key, Func<TKey, TValue> factory, Func<TKey, TValue, TValue> updater)
        {
            TValue dontCare;
            return AddOrUpdate(key, factory, updater, out dontCare);
        }
        /// <summary>Gets the value for a key, or adds one if it isn’t present.</summary>
        /// <param name="key">The key to examine or add.</param>
        /// <param name="value">The value to add for the key if it wasn’t present.</param>
        /// <param name="result">The value added or found.</param>
        /// <returns>True if the value was added, false if the key was already present.</returns>
        public bool GetOrAdd(TKey key, TValue value, out TValue result)
        {
            KV res;
            if(PutIfMatch(new KV(key, value), MatchDead.Instance, null, out res))
            {
                result = value;
                return true;
            }
            result = res.Value;
            return false;
        }
        /// <summary>Gets the value for a key, or adds one if it isn’t present.</summary>
        /// <param name="key">The key to examine or add.</param>
        /// <param name="value">The value to add for the key if it wasn’t present.</param>
        /// <returns>The value added or found.</returns>
        public TValue GetOrAdd(TKey key, TValue value)
        {
            TValue ret;
            GetOrAdd(key, value, out ret);
            return ret;
        }
        /// <summary>Gets the value for a key, or adds one if it isn’t present.</summary>
        /// <param name="key">The key to examine or add.</param>
        /// <param name="factory">A <see cref="Func&lt;T, TResult>"/> that produces a value when passed a key, to
        /// create a value if needed.</param>
        /// <param name="value">The value added or found.</param>
        /// <returns>True if the value was added, false if the key was already present.</returns>
        /// <remarks><paramref name="factory"/> will only be called if the key is absent, and will only be called once,
        /// however it does not block other threads from adding to the dictionary while it is running, so while the update
        /// will be atomic if it happens, it is possible
        /// for <paramref name="factory"/> to be called, but for the method to return an existing value.</remarks>
        public bool GetOrAdd(TKey key, Func<TKey, TValue> factory, out TValue value)
        {
            return !TryGetValue(key, out value) && GetOrAdd(key, factory(key), out value);
        }
        /// <summary>Gets the value for a key, or adds one if it isn’t present.</summary>
        /// <param name="key">The key to examine or add.</param>
        /// <param name="factory">A <see cref="Func&lt;T, TResult>"/> that produces a value when passed a key, to
        /// create a value if needed.</param>
        /// <returns>The value added or found.</returns>
        /// <remarks><paramref name="factory"/> will only be called if the key is absent, and will only be called once,
        /// however it does not block other threads from adding to the dictionary while it is running, so while the update
        /// will be atomic if it happens, it is possible
        /// for <paramref name="factory"/> to be called, but for the method to return an existing value.</remarks>
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
        {
            TValue ret;
            GetOrAdd(key, factory, out ret);
            return ret;
        }
        /// <summary>Attempts to remove an item from the collection, identified by its key.</summary>
        /// <param name="key">The key to remove.</param>
        /// <returns>True if the item was removed, false if it wasn’t found.</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.</remarks>
        public bool Remove(TKey key)
        {
            return PutIfMatch(new TombstoneKV(key), MatchLive.Instance);
        }
        /// <summary>Attempts to remove an item from the collection, identified by its key.</summary>
        /// <param name="key">The key to remove.</param>
        /// <param name="value">The value that was removed, or the default value of <c>TValue</c> if the method failed.</param>
        /// <returns>True if the item was removed, false if it wasn’t found.</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.</remarks>
        public bool Remove(TKey key, out TValue value)
        {
            KV ret;
            if(PutIfMatch(new TombstoneKV(key), MatchLive.Instance, null, out ret))
            {
                value = ret.Value;
                return true;
            }
            value = default(TValue);
            return false;
        }
        /// <summary>Attempts to retrieve the value associated with a key.</summary>
        /// <param name="key">The key searched for.</param>
        /// <param name="value">The value found (if successful).</param>
        /// <returns>True if the key was found, false otherwise.</returns>
        public bool TryGetValue(TKey key, out TValue value)
        {
            return Obtain(_table, key, Hash(key), out value);
        }
        /// <summary>Adds a key and value to the collection, as long as it is not currently present.</summary>
        /// <param name="item">The key and value to add.</param>
        /// <exception cref="System.ArgumentException">An item with the same key has already been added.</exception>
        public void Add(KeyValuePair<TKey, TValue> item)
        {
            Add(item.Key, item.Value);
        }
        /// <summary>Removes all items from the dictionary.</summary>
        /// <remarks>All items are removed in a single atomic operation.</remarks>
        public void Clear()
        {
            Table newTable = new Table(_initialCapacity, new Counter());
            Thread.MemoryBarrier();
            _table = newTable;
        }
        /// <summary>Tests whether a key and value matching that passed are present in the dictionary.</summary>
        /// <param name="item">A <see cref="System.Collections.Generic.KeyValuePair&lt;TKey,TValue>"/> defining the item sought.</param>
        /// <param name="valueComparer">An <see cref="System.Collections.Generic.IEqualityComparer&lt;T>"/> used to test a value found
        /// with that sought.</param>
        /// <returns>True if the key and value are found, false otherwise.</returns>
        public bool Contains(KeyValuePair<TKey, TValue> item, IEqualityComparer<TValue> valueComparer)
        {
            TValue test;
            return TryGetValue(item.Key, out test) && valueComparer.Equals(item.Value, test);
        }
        /// <summary>Tests whether a key and value matching that passed are present in the dictionary.</summary>
        /// <param name="item">A <see cref="System.Collections.Generic.KeyValuePair&lt;TKey,TValue>"/> defining the item sought.</param>
        /// <returns>True if the key and value are found, false otherwise.</returns>
        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return Contains(item, DefaultValCmp);
        }
        /// <summary>Copies the contents of the dictionary to an array.</summary>
        /// <param name="array">The array to copy to.</param>
        /// <param name="arrayIndex">The index within the array to start copying from</param>
        /// <exception cref="System.ArgumentNullException"/>The array was null.
        /// <exception cref="System.ArgumentOutOfRangeException"/>The array index was less than zero.
        /// <exception cref="System.ArgumentException"/>The number of items in the collection was
        /// too great to copy into the array at the index given.
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            Validation.CopyTo(array, arrayIndex);
        	Dictionary<TKey, TValue> snapshot = ToDictionary();
        	TValue valForNull;
        	if(!typeof(TKey).IsValueType && TryGetValue(default(TKey), out valForNull))
        	{
	        	if(arrayIndex + snapshot.Count + 1 > array.Length)
	        		throw new ArgumentException(Strings.Copy_To_Array_Too_Small);
	        	array[arrayIndex++] = new KeyValuePair<TKey, TValue>(default(TKey), valForNull);
        	}
        	((ICollection<KeyValuePair<TKey, TValue>>)snapshot).CopyTo(array, arrayIndex);
        }
        /// <summary>Removes an item from the collection.</summary>
        /// <param name="item">The item to remove</param>
        /// <param name="valueComparer">A <see cref="System.Collections.Generic.IEqualityComparer&lt;T>"/> that is used in considering whether
        /// an item found is equal to that searched for.</param>
        /// <param name="removed">The item removed (if successful).</param>
        /// <returns>True if the item was removed, false if no matching item was found.</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.</remarks>
        public bool Remove(KeyValuePair<TKey, TValue> item, IEqualityComparer<TValue> valueComparer, out KeyValuePair<TKey, TValue> removed)
        {
            KV rem;
            if(PutIfMatch(new TombstoneKV(item.Key), new MatchEquality(valueComparer, item.Value), null, out rem))
            {
                removed = rem;
                return true;
            }
            removed = default(KeyValuePair<TKey, TValue>);
            return false;
        }
        /// <summary>Removes an item from the collection.</summary>
        /// <param name="item">The item to remove</param>
        /// <param name="valueComparer">An <see cref="System.Collections.Generic.IEqualityComparer&lt;T>"/> used to test a value found</param>
        /// <returns>True if the item was removed, false if no matching item was found.</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.</remarks>
        public bool Remove(KeyValuePair<TKey, TValue> item, IEqualityComparer<TValue> valueComparer)
        {
            KeyValuePair<TKey, TValue> dummy;
            return Remove(item, valueComparer, out dummy);
        }
        /// <summary>Removes an item from the collection.</summary>
        /// <param name="key">The key to remove.</param>
        /// <param name="cmpValue">The value to remove.</param>
        /// <param name="valueComparer">A <see cref="System.Collections.Generic.IEqualityComparer&lt;T>"/> that is used in considering whether
        /// an item found is equal to that searched for.</param>
        /// <param name="removed">The item removed (if successful).</param>
        /// <returns>True if the item was removed, false if no matching item was found.</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.</remarks>
        public bool Remove(TKey key, TValue cmpValue, IEqualityComparer<TValue> valueComparer, out TValue removed)
        {
            KV rem;
            if(PutIfMatch(new TombstoneKV(key), new MatchEquality(valueComparer, cmpValue), null, out rem))
            {
                removed = rem.Value;
                return true;
            }
            removed = default(TValue);
            return false;
        }
        /// <summary>Removes an item from the collection.</summary>
        /// <param name="key">The key to remove.</param>
        /// <param name="cmpValue">The value to remove.</param>
        /// <param name="removed">The item removed (if successful).</param>
        /// <returns>True if the item was removed, false if no matching item was found.</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.</remarks>
        public bool Remove(TKey key, TValue cmpValue, out TValue removed)
        {
        	return Remove(key, cmpValue, DefaultValCmp, out removed);
        }
        /// <summary>Removes a <see cref="System.Collections.Generic.KeyValuePair&lt;TKey,TValue>"/> from the collection.</summary>
        /// <param name="item">The item to remove</param>
        /// <returns>True if the item was removed, false if no matching item was found.</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.</remarks>
        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            return Remove(item, DefaultValCmp);
        }
        /// <summary>Removes items from the dictionary that match a predicate.</summary>
        /// <param name="predicate">A <see cref="System.Func&lt;T1, T2, TResult>"/> that returns true for the items that should be removed.</param>
        /// <returns>A <see cref="System.Collections.Generic.IEnumerable&lt;T>"/> of the items removed.</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.
        /// <para>The returned enumerable is lazily executed, and items are only removed from the dictionary as it is enumerated.</para></remarks>
        public RemovingEnumeration RemoveWhere(Func<TKey, TValue, bool> predicate)
        {
            return new RemovingEnumeration(this, predicate);
        }
        /// <summary>Ensures that the dictionary completes all pending resize operations, and allows the keys for all removed
        /// values to be garbage collected.</summary>
        /// <remarks>
        /// <para>This method is offered purely as an optimisation for periods where the dictionary will only be read from. It will reduce
        /// performance if there are continuing writes (which would continue the resize themselves more efficiently). If the dictionary will never
        /// be written to again for the life-time of the application, it would be better still to replace it with a
        /// <see cref="Dictionary&lt;TKey, TValue>"/> as per:</para>
        /// <code>
        /// static IDictionary&lt;string, string> SharedDictionary = new LockFreeDictionary&lt;string, string>();
        /// /* 
        ///  * ⋮
        ///  */
        /// private void DoneWriting()  // Called when there will be no more writing to the dictionary.
        /// {
        ///     LockFreeDictionary&lt;string, string> lfDict = (LockFreeDictionary&lt;string, string>)SharedDictionary;
        ///     SharedDictionary = lfDict.ToDictionary();   //Create Dictionary&lt;string, string> and over-write LockFreeDictionary
        ///                                                 //as Dictionary&lt;string, string> is thread-safe when read-only.
        ///     Thread.MemoryBarrier(); //Optional. Ensure change of IDictionary is seen by other threads immediately.
        /// }
        /// </code>
        /// <para>As such, <see cref="Reduce()"/> falls into a middle-area that will rarely, if ever, arise.</para>
        /// </remarks>
        public void Reduce()
        {
            if(_table.Next == null && _table.Slots == _table.Size)
                return;
            if(_table.Next == null)
                Resize(_table);
            Table last = _table.Next;
            while(last.Next != null)
                last = last.Next;
            PrimeKV prime = new PrimeKV();
            while(_table != last)
                HelpCopy(_table, prime, true);
            if(_table.Slots > _table.Size)
            {
                Resize(_table);
                HelpCopy(_table, prime, true);
            }
        }
        /// <summary>Enumerates a <see cref="LockFreeDictionary&lt;TKey, TValue>"/>, returning items that match a predicate,
        /// and removing them from the dictionary.</summary>
        /// <threadsafety static="true" instance="false">This class is not thread-safe in itself, though its methods may be called
        /// concurrently with other operations on the same collection.</threadsafety>
        /// <tocexclude/>
        public sealed class RemovingEnumeration : IEnumerator<KeyValuePair<TKey, TValue>>, IEnumerable<KeyValuePair<TKey, TValue>>
        {
            private readonly LockFreeDictionary<TKey, TValue> _dict;
            private Table _table;
            private readonly Counter _size;
            private readonly Func<TKey, TValue, bool> _predicate;
            private int _idx;
            private int _removed;
            private KV _current;
            internal RemovingEnumeration(LockFreeDictionary<TKey, TValue> dict, Func<TKey, TValue, bool> predicate)
            {
                _size = (_table = (_dict = dict)._table).Size;
                _predicate = predicate;
                _idx = -1;
            }
            /// <summary>The current pair being enumerated.</summary>
            public KeyValuePair<TKey, TValue> Current
            {
                get { return _current; }
            }
            object IEnumerator.Current
            {
                get { return _current; }
            }
            /// <summary>Moves to the next item being enumerated.</summary>
            /// <returns>True if an item is found, false if the end of the enumeration is reached,</returns>
            public bool MoveNext()
            {
                while(_table != null)
                {
                    Record[] records = _table.Records;
                    for(++_idx; _idx != records.Length; ++_idx)
                    {
                        KV kv = records[_idx].KeyValue;
                        PrimeKV prime = kv as PrimeKV;
                        if(prime != null)
                            _dict.CopySlotsAndCheck(_table, prime, _idx);
                        else if(kv != null && !(kv is TombstoneKV) && _predicate(kv.Key, kv.Value))
                        {
                            TombstoneKV tomb = new TombstoneKV(kv.Key);
                            for(;;)
                            {
                                KV oldKV = Interlocked.CompareExchange(ref records[_idx].KeyValue, tomb, kv);
                                if(oldKV == kv)
                                {
                                    _size.Decrement();
                                    _current = kv;
                                    ++_removed;
                                    return true;
                                }
                                else if(oldKV is PrimeKV)
                                {
                                    _dict.CopySlotsAndCheck(_table, (PrimeKV)oldKV, _idx);
                                    break;
                                }
                                else if(oldKV is TombstoneKV || !_predicate(oldKV.Key, oldKV.Value))
                                    break;
                                else
                                    kv = oldKV;
                            }
                        }
                    }
                    Table next = _table.Next;
                    if(next == null)
                        ResizeIfManyRemoved();
                    _table = next;
                }
                return false;
            }
            //An optimisation, rather than necessary. If we’ve set a lot of records as tombstones,
            //then we should do well to resize now. Note that it’s safe for us to not call this
            //so we need not try-finally in internal code. Note also that it is already called if
            //we reach the end of the enumeration.
            private void ResizeIfManyRemoved()
            {
                if(_table != null && _table.Next == null && (_removed > _table.Capacity >> 4 || _removed > _table.Size >> 2))
                    LockFreeDictionary<TKey, TValue>.Resize(_table);
            }
            void IDisposable.Dispose()
            {
                ResizeIfManyRemoved();
            }
            void IEnumerator.Reset()
            {
                throw new NotSupportedException();
            }
            /// <summary>Returns the enumeration itself, used with for-each constructs as this object serves as both enumeration and eumerator.</summary>
            /// <returns>The enumeration itself.</returns>
            public RemovingEnumeration GetEnumerator()
            {
                return this;
            }
            IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
            {
                return this;
            }
            IEnumerator IEnumerable.GetEnumerator()
            {
                return this;
            }
        }
        /// <summary>Removes all key-value pairs that match a predicate.</summary>
        /// <param name="predicate">A <see cref="System.Func&lt;T1, T2, TResult>"/> that returns true when passed a key and value that should be removed.</param>
        /// <returns>The number of items removed</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.</remarks>
        public int Remove(Func<TKey, TValue, bool> predicate)
        {
        	int total = 0;
        	RemovingEnumeration remover = new RemovingEnumeration(this, predicate);
        	while(remover.MoveNext())
        	    ++total;
        	return total;
        }
        internal sealed class KVEnumerator : IEnumerator<KV>, IEnumerable<KV>
        {
        	private readonly LockFreeDictionary<TKey, TValue> _dict;
            private Table _tab;
            private KV _current;
            private int _idx = -1;
            public KVEnumerator(LockFreeDictionary<TKey, TValue> dict)
            {
            	_tab = (_dict = dict)._table;
            }
            public KV Current
            {
                get
                {
                    return _current;
                }
            }
            object IEnumerator.Current
            {
                get { return Current; }
            }
            public void Dispose(){}
            public bool MoveNext()
            {
                for(;_tab != null; _tab = _tab.Next, _idx = -1)
                {
                    Record[] records = _tab.Records;
                    for(++_idx; _idx != records.Length; ++_idx)
                    {
                        KV kv = records[_idx].KeyValue;
                        if(kv != null && !(kv is TombstoneKV))
                        {
                        	PrimeKV prime = kv as PrimeKV;
                        	if(prime != null)//part-way through being copied to next table
                        		_dict.CopySlotsAndCheck(_tab, prime, _idx);//make sure it’s there when we come to it.
                        	else
                        	{
	                            _current = kv;
	                            return true;
                        	}
                        }
                    }
                }
                return false;
            }
            public void Reset()
            {
            	_tab = _dict._table;
            	_idx = -1;
            }
            public KVEnumerator GetEnumerator()
            {
                return this;
            }
            
            IEnumerator<KV> IEnumerable<KV>.GetEnumerator()
            {
                return this;
            }
            
            IEnumerator IEnumerable.GetEnumerator()
            {
                return this;
            }
        }
        private KVEnumerator EnumerateKVs()
        {
            return new KVEnumerator(this);
        }
        /// <summary>Enumerates a LockFreeDictionary&lt;TKey, TValue>.</summary>
        /// <remarks>The use of a value type for <see cref="System.Collections.Generic.List&lt;T>.Enumerator"/> has drawn some criticism.
        /// Note that this does not apply here, as the state that changes with enumeration is not maintained by the structure itself.</remarks>
        /// <tocexclude/>
        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>, IDictionaryEnumerator
        {
            private KVEnumerator _src;
            internal Enumerator(KVEnumerator src)
            {
                _src = src;
            }
            /// <summary>Returns the current <see cref="System.Collections.Generic.KeyValuePair&lt;TKey,TValue>"/> being enumerated.</summary>
            public KeyValuePair<TKey, TValue> Current
            {
                get
                {
                    return _src.Current;
                }
            }
            object IEnumerator.Current
            {
                get
                {
                    return Current;
                }
            }
            void IDisposable.Dispose()
            {
                _src.Dispose();
            }
            /// <summary>Moves to the next item in the enumeration.</summary>
            /// <returns>True if another item was found, false if the end of the enumeration was reached.</returns>
            public bool MoveNext()
            {
                return _src.MoveNext();
            }
            /// <summary>Reset the enumeration.</summary>
            public void Reset()
            {
                _src.Reset();
            }
            object IDictionaryEnumerator.Key
            {
                get { return Current.Key; }
            }
            object IDictionaryEnumerator.Value
            {
                get { return Current.Value; }
            }
            DictionaryEntry IDictionaryEnumerator.Entry
            {
                get { return new DictionaryEntry(Current.Key, Current.Value); }
            }
        }
        /// <summary>Returns an enumerator that iterates through the collection.</summary>
        /// <returns>The enumerator.</returns>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(EnumerateKVs());
        }
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
        {
            return GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
        /// <summary>A collection of the values in a LockFreeDictionary.</summary>
        /// <remarks>The collection is "live" and immediately reflects changes in the dictionary.</remarks>
        /// <threadsafety static="true" instance="true"/>
        /// <tocexclude/>
	    public struct ValueCollection : ICollection<TValue>, ICollection
	    {
	    	private readonly LockFreeDictionary<TKey, TValue> _dict;
	    	internal ValueCollection(LockFreeDictionary<TKey, TValue> dict)
	    	{
	    		_dict = dict;
	    	}
	    	/// <summary>The number of items in the collection.</summary>
			public int Count
			{
				get { return _dict.Count; }
			}
			bool ICollection<TValue>.IsReadOnly
			{
				get { return true; }
			}
			/// <summary>Tests the collection for the presence of an item.</summary>
			/// <param name="item">The item to search for.</param>
			/// <param name="cmp">An <see cref="System.Collections.Generic.IEqualityComparer&lt;T>"/> to use in comparing
			/// items found with that sought.</param>
			/// <returns>True if a matching item  was found, false otherwise.</returns>
			public bool Contains(TValue item, IEqualityComparer<TValue> cmp)
			{
				foreach(TValue val in this)
					if(cmp.Equals(item, val))
						return true;
				return false;
			}
			/// <summary>Tests the collection for the presence of an item.</summary>
			/// <param name="item">The item to search for.</param>
			/// <returns>True if a matching item  was found, false otherwise.</returns>
			public bool Contains(TValue item)
			{
				return Contains(item, DefaultValCmp);
			}
            /// <summary>Copies the contents of the collection to an array.</summary>
            /// <param name="array">The array to copy to.</param>
            /// <param name="arrayIndex">The index within the array to start copying from</param>
            /// <exception cref="System.ArgumentNullException"/>The array was null.
            /// <exception cref="System.ArgumentOutOfRangeException"/>The array index was less than zero.
            /// <exception cref="System.ArgumentException"/>The number of items in the collection was
            /// too great to copy into the array at the index given.
			public void CopyTo(TValue[] array, int arrayIndex)
			{
			    Validation.CopyTo(array, arrayIndex);
	        	Dictionary<TKey, TValue> snapshot = _dict.ToDictionary();
	        	TValue valForNull;
	        	if(!typeof(TKey).IsValueType && _dict.TryGetValue(default(TKey), out valForNull))
	        	{
		        	if(arrayIndex + snapshot.Count + 1 > array.Length)
		        		throw new ArgumentException(Strings.Copy_To_Array_Too_Small);
		        	array[arrayIndex++] = valForNull;
	        	}
	        	snapshot.Values.CopyTo(array, arrayIndex);
			}
			/// <summary>Enumerates a value collection.</summary>
            /// <remarks>The use of a value type for <see cref="System.Collections.Generic.List&lt;T>.Enumerator"/> has drawn some criticism.
            /// Note that this does not apply here, as the state that changes with enumeration is not maintained by the structure itself.</remarks>
            /// <threadsafety static="true" instance="false">This class is not thread-safe in itself, though its methods may be called
            /// concurrently with other operations on the same collection.</threadsafety>
			public struct Enumerator : IEnumerator<TValue>
			{
	            private KVEnumerator _src;
	            internal Enumerator(KVEnumerator src)
	            {
	                _src = src;
	            }
	            /// <summary>Returns the current value being enumerated.</summary>
	            public TValue Current
	            {
	                get
	                {
	                    return _src.Current.Value;
	                }
	            }
	            object IEnumerator.Current
	            {
	                get
	                {
	                    return Current;
	                }
	            }
	            void IDisposable.Dispose()
	            {
	                _src.Dispose();
	            }
	            /// <summary>Moves to the next item being iterated.</summary>
	            /// <returns>True if another item is found, false if the end of the collection is reached.</returns>
	            public bool MoveNext()
	            {
	                return _src.MoveNext();
	            }
	            /// <summary>Reset the enumeration.</summary>
	            public void Reset()
	            {
	                _src.Reset();
	            }
			}
			/// <summary>Returns an enumerator that iterates through the collection.</summary>
			/// <returns>The <see cref="Enumerator"/> that performs the iteration.</returns>
			public Enumerator GetEnumerator()
			{
				return new Enumerator(_dict.EnumerateKVs());
			}
			IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator()
			{
			    return GetEnumerator();
			}
			IEnumerator IEnumerable.GetEnumerator()
			{
				return GetEnumerator();
			}
			void ICollection<TValue>.Add(TValue item)
			{
				throw new NotSupportedException();
			}
			void ICollection<TValue>.Clear()
			{
				throw new NotSupportedException();
			}
			bool ICollection<TValue>.Remove(TValue item)
			{
				throw new NotSupportedException();
			}
            object ICollection.SyncRoot
            {
                get { throw new NotSupportedException(Strings.SyncRoot_Not_Supported); }
            }	        
            bool ICollection.IsSynchronized
            {
                get { return false; }
            }
            void ICollection.CopyTo(Array array, int index)
            {
                Validation.CopyTo(array, index);
	        	((ICollection)_dict.ToDictionary().Values).CopyTo(array, index);
            }
	    }
        /// <summary>A collection of the keys in a LockFreeDictionary.</summary>
        /// <remarks>The collection is "live" and immediately reflects changes in the dictionary.</remarks>
        /// <threadsafety static="true" instance="true"/>
        /// <tocexclude/>
	    public struct KeyCollection : ICollection<TKey>, ICollection
	    {
	    	private readonly LockFreeDictionary<TKey, TValue> _dict;
	    	internal KeyCollection(LockFreeDictionary<TKey, TValue> dict)
	    	{
	    		_dict = dict;
	    	}
	    	/// <summary>The number of items in the collection.</summary>
			public int Count
			{
				get { return _dict.Count; }
			}
			bool ICollection<TKey>.IsReadOnly
			{
				get { return true; }
			}
			/// <summary>Tests for the presence of a key in the collection.</summary>
			/// <param name="item">The key to search for.</param>
			/// <returns>True if the key is found, false otherwise.</returns>
			public bool Contains(TKey item)
			{
				return _dict.ContainsKey(item);
			}
            /// <summary>Copies the contents of the dictionary to an array.</summary>
            /// <param name="array">The array to copy to.</param>
            /// <param name="arrayIndex">The index within the array to start copying from</param>
            /// <exception cref="System.ArgumentNullException"/>The array was null.
            /// <exception cref="System.ArgumentOutOfRangeException"/>The array index was less than zero.
            /// <exception cref="System.ArgumentException"/>The number of items in the collection was
            /// too great to copy into the array at the index given.
			public void CopyTo(TKey[] array, int arrayIndex)
			{
			    Validation.CopyTo(array, arrayIndex);
	        	Dictionary<TKey, TValue> snapshot = _dict.ToDictionary();
	        	if(!typeof(TKey).IsValueType && _dict.ContainsKey(default(TKey)))
	        	{
		        	if(arrayIndex + snapshot.Count + 1 > array.Length)
		        		throw new ArgumentException(Strings.Copy_To_Array_Too_Small);
	        		array[arrayIndex++] = default(TKey);
	        	}
	        	snapshot.Keys.CopyTo(array, arrayIndex);
			}
			/// <summary>Enumerates a key collection.</summary>
            /// <remarks>The use of a value type for <see cref="System.Collections.Generic.List&lt;T>.Enumerator"/> has drawn some criticism.
            /// Note that this does not apply here, as the state that changes with enumeration is not maintained by the structure itself.</remarks>
            /// <threadsafety static="true" instance="false">This class is not thread-safe in itself, though its methods may be called
            /// concurrently with other operations on the same collection.</threadsafety>
			public struct Enumerator : IEnumerator<TKey>
			{
	            private KVEnumerator _src;
	            internal Enumerator(KVEnumerator src)
	            {
	                _src = src;
	            }
	            /// <summary>Returns the current item being enumerated.</summary>
	            public TKey Current
	            {
	                get
	                {
	                    return _src.Current.Key;
	                }
	            }
	            object IEnumerator.Current
	            {
	                get
	                {
	                    return Current;
	                }
	            }
	            void IDisposable.Dispose()
	            {
	                _src.Dispose();
	            }
                /// <summary>Moves to the next item in the enumeration.</summary>
                /// <returns>True if another item was found, false if the end of the enumeration was reached.</returns>
	            public bool MoveNext()
	            {
	                return _src.MoveNext();
	            }
	            /// <summary>Reset the enumeration.</summary>
	            public void Reset()
	            {
	                _src.Reset();
	            }
			}
            /// <summary>Returns an enumerator that iterates through the collection.</summary>
            /// <returns>The enumerator.</returns>
			public Enumerator GetEnumerator()
			{
				return new Enumerator(_dict.EnumerateKVs());
			}
			IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator()
			{
			    return GetEnumerator();
			}
			IEnumerator IEnumerable.GetEnumerator()
			{
				return GetEnumerator();
			}
			void ICollection<TKey>.Add(TKey item)
			{
				throw new NotSupportedException();
			}
			void ICollection<TKey>.Clear()
			{
				throw new NotSupportedException();
			}
			bool ICollection<TKey>.Remove(TKey item)
			{
				throw new NotSupportedException();
			}
            object ICollection.SyncRoot
            {
                get { throw new NotSupportedException(Strings.SyncRoot_Not_Supported); }
            }
	        
            bool ICollection.IsSynchronized
            {
                get { return false; }
            }
            void ICollection.CopyTo(Array array, int index)
            {
                Validation.CopyTo(array, index);
	        	Dictionary<TKey, TValue> snapshot = _dict.ToDictionary();
	        	((ICollection)_dict.ToDictionary().Keys).CopyTo(array, index);
            }
	    }
	    object IDictionary.this[object key]
        {
            get
            {
                TValue ret;
                if(typeof(TKey).IsValueType && key == null)
                    return null;
                if(key is TKey || key == null)
                    return TryGetValue((TKey)key, out ret) ? (object)ret : null;
                return null;
            }
            set
            {
                if(typeof(TKey).IsValueType && key == null)
                    throw new ArgumentException(Strings.Cant_Cast_Null_To_Value_Type(typeof(TKey)), "key");
                if(typeof(TValue).IsValueType && value == null)
                    throw new ArgumentException(Strings.Cant_Cast_Null_To_Value_Type(typeof(TValue)), "value");
                try
                {
                    TKey convKey = (TKey)key;
                    try
                    {
                        this[convKey] = (TValue)value;
                    }
                    catch(InvalidCastException)
                    {
                        throw new ArgumentException(Strings.Invalid_Cast_Values(value.GetType(), typeof(TValue)), "value");
                    }
                }
                catch(InvalidCastException)
                {
                    throw new ArgumentException(Strings.Invalid_Cast_Keys(key.GetType(), typeof(TKey)), "key");
                }
            }
        }
        
        ICollection IDictionary.Keys
        {
            get { return Keys; }
        }
        
        ICollection IDictionary.Values
        {
            get { return Values; }
        }
        
        bool IDictionary.IsFixedSize
        {
            get { return false; }
        }
        
        object ICollection.SyncRoot
        {
            get { throw new NotSupportedException(Strings.SyncRoot_Not_Supported); }
        }
        
        bool ICollection.IsSynchronized
        {
            get { return false; }
        }
        
        bool IDictionary.Contains(object key)
        {
            if(key == null)
                return !typeof(TKey).IsValueType && ContainsKey(default(TKey));
            return key is TKey && ContainsKey((TKey)key);
        }
        
        void IDictionary.Add(object key, object value)
        {
            if(typeof(TKey).IsValueType && key == null)
                throw new ArgumentException(Strings.Cant_Cast_Null_To_Value_Type(typeof(TKey)), "key");
            if(typeof(TValue).IsValueType && value == null)
                throw new ArgumentException(Strings.Cant_Cast_Null_To_Value_Type(typeof(TValue)), "value");
            try
            {
                TKey convKey = (TKey)key;
                try
                {
                    Add(convKey, (TValue)value);
                }
                catch(InvalidCastException)
                {
                    throw new ArgumentException(Strings.Invalid_Cast_Values(value.GetType(), typeof(TValue)), "value");
                }
            }
            catch(InvalidCastException)
            {
                throw new ArgumentException(Strings.Invalid_Cast_Keys(key.GetType(), typeof(TKey)), "key");
            }
        }
        
        IDictionaryEnumerator IDictionary.GetEnumerator()
        {
            return GetEnumerator();
        }
        void IDictionary.Remove(object key)
        {
            if(typeof(TKey).IsValueType && key == null)
                return;
            if(key == null || key is TKey)
                Remove((TKey)key);
        }
        
        void ICollection.CopyTo(Array array, int index)
        {
            Validation.CopyTo(array, index);
        	((ICollection)ToDictionary()).CopyTo(array, index);
        }
    }
}