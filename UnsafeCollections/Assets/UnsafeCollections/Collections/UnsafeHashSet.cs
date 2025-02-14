/*
The MIT License (MIT)

Copyright (c) 2019 Fredrik Holmstrom

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

using System;

namespace Collections.Unsafe {
  public unsafe partial struct UnsafeHashSet {
    UnsafeHashCollection _collection;

    public static UnsafeHashSet* Allocate<T>(int capacity, bool fixedSize = false)
      where T : unmanaged, IEquatable<T> {
      return Allocate(capacity, sizeof(T), fixedSize);
    }

    public static UnsafeHashSet* Allocate(int capacity, int valStride, bool fixedSize = false) {
      var entryStride = sizeof(UnsafeHashCollection.Entry);

      // round capacity up to next prime 
      capacity = UnsafeHashCollection.GetNextPrime(capacity);

      // this has to be true
      Assert.Check(entryStride == 16);

      var valAlignment = AllocHelper.GetAlignmentForArrayElement(valStride);

      // the alignment for entry/key/val, we can't have less than ENTRY_ALIGNMENT
      // bytes alignment because entries are 16 bytes with 1 x pointer + 2 x 4 byte integers
      var alignment = Math.Max(UnsafeHashCollection.Entry.ALIGNMENT, valAlignment);

      // calculate strides for all elements
      valStride   = AllocHelper.RoundUpToAlignment(valStride,                          alignment);
      entryStride = AllocHelper.RoundUpToAlignment(sizeof(UnsafeHashCollection.Entry), alignment);

      // dictionary ptr
      UnsafeHashSet* set;

      if (fixedSize) {
        var sizeOfHeader        = AllocHelper.RoundUpToAlignment(sizeof(UnsafeHashSet),                           alignment);
        var sizeOfBucketsBuffer = AllocHelper.RoundUpToAlignment(sizeof(UnsafeHashCollection.Entry**) * capacity, alignment);
        var sizeofEntriesBuffer = (entryStride + valStride) * capacity;

        // allocate memory
        var ptr = AllocHelper.MallocAndClear(sizeOfHeader + sizeOfBucketsBuffer + sizeofEntriesBuffer, alignment);

        // start of memory is the dict itself
        set = (UnsafeHashSet*)ptr;

        // buckets are offset by header size
        set->_collection.Buckets = (UnsafeHashCollection.Entry**)((byte*)ptr + sizeOfHeader);

        // initialize fixed buffer
        UnsafeBuffer.InitFixed(&set->_collection.Entries, (byte*)ptr + (sizeOfHeader + sizeOfBucketsBuffer), capacity, entryStride + valStride);
      }
      else {
        // allocate dict, buckets and entries buffer separately
        set                      = AllocHelper.MallocAndClear<UnsafeHashSet>();
        set->_collection.Buckets = (UnsafeHashCollection.Entry**)AllocHelper.MallocAndClear(sizeof(UnsafeHashCollection.Entry**) * capacity, sizeof(UnsafeHashCollection.Entry**));

        // init dynamic buffer
        UnsafeBuffer.InitDynamic(&set->_collection.Entries, capacity, entryStride + valStride);
      }

      set->_collection.FreeCount = 0;
      set->_collection.UsedCount = 0;
      set->_collection.KeyOffset = entryStride;

      return set;
    }

    public static int Capacity(UnsafeHashSet* set) {
      return set->_collection.Entries.Length;
    }

    public static int Count(UnsafeHashSet* set) {
      return set->_collection.UsedCount - set->_collection.FreeCount;
    }

    public static bool Add<T>(UnsafeHashSet* set, T key)
      where T : unmanaged, IEquatable<T> {
      var hash  = key.GetHashCode();
      var entry = UnsafeHashCollection.Find<T>(&set->_collection, key, hash);
      if (entry == null) {
        UnsafeHashCollection.Insert<T>(&set->_collection, key, hash);
        return true;
      }

      return false;
    }

    public static bool Remove<T>(UnsafeHashSet* set, T key) where T : unmanaged, IEquatable<T> {
      return UnsafeHashCollection.Remove<T>(&set->_collection, key, key.GetHashCode());
    }

    public static bool Contains<T>(UnsafeHashSet* set, T key) where T : unmanaged, IEquatable<T> {
      return UnsafeHashCollection.Find<T>(&set->_collection, key, key.GetHashCode()) != null;
    }

    public static Iterator<T> GetIterator<T>(UnsafeHashSet* set) where T : unmanaged {
      return new Iterator<T>(set);
    }

    public static void And<T>(UnsafeHashSet* set, UnsafeHashSet* other) where T : unmanaged, IEquatable<T> {
      for (int i = set->_collection.UsedCount - 1; i >= 0; --i) {
        var entry = UnsafeHashCollection.GetEntry(&set->_collection, i);
        if (entry->State == UnsafeHashCollection.EntryState.Used) {
          var key     = *(T*)((byte*)entry + set->_collection.KeyOffset);
          var keyHash = key.GetHashCode();

          // if we don't find this in other collection, remove it (And)
          if (UnsafeHashCollection.Find<T>(&other->_collection, key, keyHash) == null) {
            UnsafeHashCollection.Remove<T>(&set->_collection, key, keyHash);
          }
        }
      }
    }

    public static void Or<T>(UnsafeHashSet* set, UnsafeHashSet* other) where T : unmanaged, IEquatable<T> {
      for (int i = other->_collection.UsedCount - 1; i >= 0; --i) {
        var entry = UnsafeHashCollection.GetEntry(&other->_collection, i);
        if (entry->State == UnsafeHashCollection.EntryState.Used) {
          // always add to this collection
          Add<T>(set, *(T*)((byte*)entry + other->_collection.KeyOffset));
        }
      }
    }

    public static void Xor<T>(UnsafeHashSet* set, UnsafeHashSet* other) where T : unmanaged, IEquatable<T> {
      for (int i = other->_collection.UsedCount - 1; i >= 0; --i) {
        var entry = UnsafeHashCollection.GetEntry(&other->_collection, i);
        if (entry->State == UnsafeHashCollection.EntryState.Used) {
          var key     = *(T*)((byte*)entry + other->_collection.KeyOffset);
          var keyHash = key.GetHashCode();

          // if we don't find it in our collection, add it
          if (UnsafeHashCollection.Find<T>(&set->_collection, key, keyHash) == null) {
            UnsafeHashCollection.Insert<T>(&set->_collection, key, keyHash);
          }

          // if we do, remove it
          else {
            UnsafeHashCollection.Remove<T>(&set->_collection, key, keyHash);
          }
        }
      }
    }
  }
}