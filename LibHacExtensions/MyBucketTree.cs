﻿using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace nsZip.LibHacExtensions
{
	public class MyBucketTree<T> where T : MyBucketTreeEntry<T>, new()
	{
		private const int BucketAlignment = 0x4000;

		public MyBucketTree(Stream header, Stream data, long EncryptionHeaderAbsolutOffset)
		{
			var realDataPos = data.Position;
			data.Position = EncryptionHeaderAbsolutOffset;
			Header = new MyBucketTreeHeader(header);
			var reader = new BinaryReader(data);

			BucketOffsets = new MyBucketTreeBucket<OffsetEntry>(reader);

			Buckets = new MyBucketTreeBucket<T>[BucketOffsets.EntryCount];

			for (var i = 0; i < BucketOffsets.EntryCount; ++i)
			{
				reader.BaseStream.Position = EncryptionHeaderAbsolutOffset + (i + 1) * BucketAlignment;
				Buckets[i] = new MyBucketTreeBucket<T>(reader);
			}

			data.Position = realDataPos;
		}

		public MyBucketTreeHeader Header { get; }
		public MyBucketTreeBucket<OffsetEntry> BucketOffsets { get; }
		public MyBucketTreeBucket<T>[] Buckets { get; }

		public List<T> GetEntryList()
		{
			var list = Buckets.SelectMany(x => x.Entries).ToList();

			for (var i = 0; i < list.Count - 1; i++)
			{
				list[i].Next = list[i + 1];
				list[i].OffsetEnd = list[i + 1].Offset;
			}

			list[list.Count - 1].OffsetEnd = BucketOffsets.OffsetEnd;

			return list;
		}
	}

	public class MyBucketTreeHeader
	{
		public int Field1C;
		public string Magic;
		public int NumEntries;
		public int Version;

		public MyBucketTreeHeader(Stream storage)
		{
			var reader = new BinaryReader(storage);

			Magic = reader.ReadAscii(4);
			Version = reader.ReadInt32();
			NumEntries = reader.ReadInt32();
			Field1C = reader.ReadInt32();
		}
	}

	public class MyBucketTreeBucket<T> where T : MyBucketTreeEntry<T>, new()
	{
		public T[] Entries;
		public int EntryCount;
		public int Index;
		public long OffsetEnd;

		public MyBucketTreeBucket(BinaryReader reader)
		{
			Index = reader.ReadInt32();
			EntryCount = reader.ReadInt32();
			OffsetEnd = reader.ReadInt64();
			Entries = new T[EntryCount];

			for (var i = 0; i < EntryCount; i++)
			{
				Entries[i] = new T().Read(reader);
			}
		}
	}

	public abstract class MyBucketTreeEntry<T> where T : MyBucketTreeEntry<T>
	{
		public long Offset { get; set; }
		public long OffsetEnd { get; set; }
		public T Next { get; set; }

		protected abstract void ReadSpecific(BinaryReader reader);

		internal T Read(BinaryReader reader)
		{
			Offset = reader.ReadInt64();
			ReadSpecific(reader);
			return (T) this;
		}
	}

	public class OffsetEntry : MyBucketTreeEntry<OffsetEntry>
	{
		protected override void ReadSpecific(BinaryReader reader)
		{
		}
	}

	public class AesSubsectionEntry : MyBucketTreeEntry<AesSubsectionEntry>
	{
		public uint Field8 { get; set; }
		public uint Counter { get; set; }

		protected override void ReadSpecific(BinaryReader reader)
		{
			Field8 = reader.ReadUInt32();
			Counter = reader.ReadUInt32();
		}
	}

	public class RelocationEntry : MyBucketTreeEntry<RelocationEntry>
	{
		public long SourceOffset { get; set; }
		public int SourceIndex { get; set; }

		protected override void ReadSpecific(BinaryReader reader)
		{
			SourceOffset = reader.ReadInt64();
			SourceIndex = reader.ReadInt32();
		}
	}
}