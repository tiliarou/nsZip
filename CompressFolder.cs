﻿using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LibHac;
using LibHac.Fs;
using nsZip.LibHacExtensions;
using Zstandard.Net;

namespace nsZip
{
	internal class CompressFolder
	{
		private static readonly RNGCryptoServiceProvider secureRNG = new RNGCryptoServiceProvider();
		private readonly int bs;
		private readonly string inFolderPath;
		private readonly Output Out;
		private readonly string outFolderPath;
		private readonly int ZstdLevel;
		private readonly int MaxDegreeOfParallelism;
		private int amountOfBlocks;
		private byte[] nsZipHeader;
		private SHA256Cng sha256Compressed;
		private SHA256Cng sha256Header;
		private int sizeOfSize;

		private CompressFolder(Output OutArg, string inFolderPathArg, string outFolderPathArg,
			int bsArg, int ZstdLevelArg, int MaxDegreeOfParallelismArg)
		{
			bs = bsArg;
			ZstdLevel = ZstdLevelArg;
			MaxDegreeOfParallelism = MaxDegreeOfParallelismArg;
			Out = OutArg;
			inFolderPath = inFolderPathArg;
			outFolderPath = outFolderPathArg;

			if(MaxDegreeOfParallelism == 0 || MaxDegreeOfParallelism > Environment.ProcessorCount)
			{
				MaxDegreeOfParallelism = Environment.ProcessorCount;
			}

			if(!Environment.Is64BitProcess)
			{
				MaxDegreeOfParallelism = Math.Min(16, MaxDegreeOfParallelism);
			}

			CompressFunct();
		}

		public static void Compress(Output OutArg, string inFolderPathArg, string outFolderPathArg,
			int bsArg, int ZstdLevel, int MaxDegreeOfParallelismArg)
		{
			new CompressFolder(OutArg, inFolderPathArg, outFolderPathArg, bsArg, ZstdLevel, MaxDegreeOfParallelismArg);
		}

		private static String GetFullPathWithoutExtension(String path)
		{
			return Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
		}

		private void CompressFunct()
		{
			var CompressionIO = new byte[104857600];
			var blocksPerChunk = CompressionIO.Length / bs + (CompressionIO.Length % bs > 0 ? 1 : 0);
			var sourceFs = new LocalFileSystem(inFolderPath);
			var destFs = new LocalFileSystem(outFolderPath);
			foreach (var file in sourceFs.EnumerateEntries().Where(item => item.Type == DirectoryEntryType.File))
			{
				Out.Log($"{file.FullPath}\r\n");
				var outFileName = $"{file.Name}.nsz";
				using (var outputFileBase = FolderTools.CreateAndOpen(file, destFs, outFileName))
				using (var outputFile = new FilePositionStorage(outputFileBase))
				using (var inputFileBase = sourceFs.OpenFile(file.FullPath, OpenMode.Read))
				using (var inputFile = new FilePositionStorage(inputFileBase))
				{
					amountOfBlocks = (int)Math.Ceiling((decimal)inputFile.GetSize() / bs);
					sizeOfSize = (int)Math.Ceiling(Math.Log(bs, 2) / 8);
					var perBlockHeaderSize = sizeOfSize + 1;
					var headerSize = 0x15 + perBlockHeaderSize * amountOfBlocks;
					outputFile.Seek(headerSize);
					var nsZipMagic = new byte[] { 0x6e, 0x73, 0x5a, 0x69, 0x70 };
					var nsZipMagicRandomKey = new byte[5];
					secureRNG.GetBytes(nsZipMagicRandomKey);
					Util.XorArrays(nsZipMagic, nsZipMagicRandomKey);
					var chunkIndex = 0;
					nsZipHeader = new byte[headerSize];
					Array.Copy(nsZipMagic, 0x00, nsZipHeader, 0x00, 0x05);
					Array.Copy(nsZipMagicRandomKey, 0x00, nsZipHeader, 0x05, 0x05);
					nsZipHeader[0x0A] = 0x00; //Version
					nsZipHeader[0x0B] = 0x01; //Type
					nsZipHeader[0x0C] = (byte)(bs >> 32);
					nsZipHeader[0x0D] = (byte)(bs >> 24);
					nsZipHeader[0x0E] = (byte)(bs >> 16);
					nsZipHeader[0x0F] = (byte)(bs >> 8);
					nsZipHeader[0x10] = (byte)bs;
					nsZipHeader[0x11] = (byte)(amountOfBlocks >> 24);
					nsZipHeader[0x12] = (byte)(amountOfBlocks >> 16);
					nsZipHeader[0x13] = (byte)(amountOfBlocks >> 8);
					nsZipHeader[0x14] = (byte)amountOfBlocks;
					sha256Compressed = new SHA256Cng();


					long maxPos = inputFile.GetSize();
					int blocksLeft;
					int blocksInThisChunk;

					do
					{
						var outputLen = new int[blocksPerChunk]; //Filled with 0
						inputFile.Read(CompressionIO);

						blocksLeft = amountOfBlocks - chunkIndex * blocksPerChunk;
						blocksInThisChunk = Math.Min(blocksPerChunk, blocksLeft);

						var opt = new ParallelOptions() { MaxDegreeOfParallelism = this.MaxDegreeOfParallelism };

						//for(int index = 0; index < blocksInThisChunk; ++index)
						Parallel.For(0, blocksInThisChunk, opt, index =>
						{
							var currentBlockID = chunkIndex * blocksPerChunk + index;
							var startPosRelative = index * bs;

							//Don't directly cast bytesLeft to int or sectors over 2 GB will overflow into negative size
							long startPos = (long)currentBlockID * (long)bs;
							long bytesLeft = maxPos - startPos;
							var blockSize = bs < bytesLeft ? bs : (int)bytesLeft;

							Out.Print($"Block: {currentBlockID + 1}/{amountOfBlocks} ({opt.MaxDegreeOfParallelism})\r\n");

							CompressionAlgorithm compressionAlgorithm;
							outputLen[index] = CompressBlock(ref CompressionIO, startPosRelative, blockSize, out compressionAlgorithm);
							//Out.Log($"inputLen[{currentBlockID}]: {blockSize}\r\n");
							//Out.Log($"outputLen[{currentBlockID}]: {outputLen[index]} bytesLeft={bytesLeft}\r\n");

							var offset = currentBlockID * (sizeOfSize + 1);
							switch (compressionAlgorithm)
							{
								case CompressionAlgorithm.None:
									nsZipHeader[0x15 + offset] = 0x00;
									break;
								case CompressionAlgorithm.Zstandard:
									nsZipHeader[0x15 + offset] = 0x01;
									break;
								case CompressionAlgorithm.LZMA:
									nsZipHeader[0x15 + offset] = 0x02;
									break;
								default:
									throw new ArgumentOutOfRangeException();
							}
							for (var j = 0; j < sizeOfSize; ++j)
							{
								nsZipHeader[0x16 + offset + j] = (byte)(outputLen[index] >> ((sizeOfSize - j - 1) * 8));
							}

						});

						for (int index = 0; index < blocksInThisChunk; ++index)
						{
							var startPos = index * bs;
							sha256Compressed.TransformBlock(CompressionIO, startPos, outputLen[index], null, 0);
							var dataToWrite = CompressionIO.AsSpan().Slice(startPos, outputLen[index]);
							outputFile.Write(dataToWrite);
						}

						++chunkIndex;
					} while (blocksLeft - blocksInThisChunk > 0);

					outputFile.Write(nsZipHeader, 0);
					sha256Header = new SHA256Cng();
					sha256Header.ComputeHash(nsZipHeader);
					var sha256Hash = new byte[0x20];
					Array.Copy(sha256Header.Hash, sha256Hash, 0x20);
					sha256Compressed.TransformFinalBlock(new byte[0], 0, 0);
					Util.XorArrays(sha256Hash, sha256Compressed.Hash);
					//Console.WriteLine(sha256Header.Hash.ToHexString());
					//Console.WriteLine(sha256Compressed.Hash.ToHexString());
					outputFile.Seek(0, SeekOrigin.End);
					outputFile.Write(sha256Hash.AsSpan().Slice(0, 0x10));
				}
			}
		}



		enum CompressionAlgorithm { None, Zstandard, LZMA };

		private int CompressBlock(ref byte[] input, int startPos, int blockSize, out CompressionAlgorithm compressionAlgorithm)
		{
			if(ZstdLevel < 1)
			{
				compressionAlgorithm = CompressionAlgorithm.None;
				return blockSize;
			}

			// compress
			using (var memoryStream = new MemoryStream())
			using (var compressionStream = new ZstandardStream(memoryStream, CompressionMode.Compress))
			{
				compressionStream.CompressionLevel = ZstdLevel;
				compressionStream.Write(input, startPos, blockSize);
				compressionStream.Close();
				var tmp = memoryStream.ToArray();
				if (tmp.Length < blockSize)
				{
					compressionAlgorithm = CompressionAlgorithm.Zstandard;
					Array.Copy(tmp, 0, input, startPos, tmp.Length);
					return tmp.Length;
				}

				compressionAlgorithm = CompressionAlgorithm.None;
				return blockSize;
			}

			
		}
	}
}