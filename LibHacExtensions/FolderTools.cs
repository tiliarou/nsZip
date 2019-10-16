﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibHac;
using LibHac.Fs;

namespace nsZip.LibHacExtensions
{
	public static class FolderTools
	{
		public static void FolderToNSP(IFileSystem inFolderFs, IFile outFile)
		{
			using (var nspBuilder = new PartitionFileSystemBuilder(inFolderFs))
			using (var newNSP = nspBuilder.Build(PartitionFileSystemType.Standard))
			using (var outFileWriter = new FilePositionStorage(outFile, true))
			{
				newNSP.CopyTo(outFileWriter);
			}
		}

		public static void FolderToXCI(IFileSystem inFolder, string nspFile, Keyset keyset)
		{
			foreach (var folder in inFolder.OpenDirectory("/", OpenDirectoryMode.Directories).Read())
			{
				Console.WriteLine(folder.FullPath);
				using (var outfile = new FileStream(folder.Name, FileMode.Create, FileAccess.Write))
				{
					using (var hfs0Storage = FolderToHFS0(new SubdirectoryFileSystem(inFolder, folder.FullPath)))
					{
						hfs0Storage.CopyToStream(outfile);
					}
				}
			}

			return;
			using (var outfile = new FileStream(nspFile, FileMode.Create, FileAccess.Write))
			{
				XciHeader xciHeader;
				using (var metaFile = File.Open($"{inFolder}/../xciMeta.dat", FileMode.Open, FileAccess.Read).AsStorage())
				{
					var expectedHeader = new byte[] { 0x6e, 0x73, 0x5a, 0x69, 0x70, 0x4d, 0x65, 0x74, 0x61, 0x58, 0x43, 0x49 };
					var xciMetaHeader = new byte[12];
					metaFile.Read(xciMetaHeader, 0);
					var xciMetaVersion = new byte[1];
					metaFile.Read(xciMetaVersion, 0xC);
					if (xciMetaVersion[0] == 0x00)
					{
						throw new InvalidDataException(
							"XCIZs created before nsZip 2.0.0 aren’t supported because their header and cert is missing due to a bug." +
							"Sorry that this was broken and all previously made XCIZ can't be converted back into clean XCI files." +
							"To be fair XCIZ => to XCI wasn't implemented and XCIZ only experimental but it still sucks." +
							"I hope nobody covered all his dumps into XCIZ but if you did feel free to open an issue and I will" +
							"probably add unclean XCIZ to XCI support by using fake headers like the NSP to XCI tools do.");
					}
					else if (xciMetaVersion[0] != 0x01)
					{
						throw new InvalidDataException("This XCIZ file is too new for this version of nsZip. Please use the latest version of nsZip instead.");
					}

					var xciHeaderData = new byte[0x200];
					var xciCertData = new byte[0x200];

					metaFile.Read(xciHeaderData, 0xD);
					using (var memoryStream = new MemoryStream(xciHeaderData))
					{
						xciHeader = new XciHeader(keyset, memoryStream);
						outfile.Write(xciHeaderData, 0, 0x200);

						metaFile.Read(xciCertData, 0x20D);
						outfile.Seek(0x7000, SeekOrigin.Begin);
						outfile.Write(xciCertData, 0, 0x200);

						var fillLeangth = xciHeader.RootPartitionOffset - 0x7200;
						var fillData = new byte[fillLeangth];
						fillData.AsSpan().Fill(0xFF);
						outfile.Write(fillData, 0, (int)fillLeangth);
					}
				}

				var xciMetaFileInfo = new FileInfo($"{inFolder}/xciMeta.dat");
				xciMetaFileInfo.Delete();
				using (var hfs0Storage = FolderToHFS0(inFolder))
				{
					hfs0Storage.CopyToStream(outfile);
				}
			}
		}

		public static IStorage FolderToHFS0(IFileSystem inFolderFs)
		{
			var hfs0Builder = new PartitionFileSystemBuilder(inFolderFs);
			return hfs0Builder.Build(PartitionFileSystemType.Hashed);
		}

		public static void ExtractTitlekeys(string inFolder, Keyset keyset, Output Out)
		{
			var dirExtracted = new DirectoryInfo(inFolder);
			var TikFiles = dirExtracted.GetFiles("*.tik");
			foreach (var file in TikFiles)
			{
				using (var TicketFile = File.Open($"{inFolder}/{file.Name}", FileMode.Open))
				{
					TitleKeyTools.ExtractKey(TicketFile, file.Name, keyset, Out);
				}
			}
		}

		public static IFile CreateAndOpen(DirectoryEntry srcFile, IFileSystem destFs, string filename, long size = 0)
		{
			var baseDir = srcFile.FullPath.Substring(0, srcFile.FullPath.LastIndexOf('/') + 1);
			destFs.EnsureDirectoryExists(baseDir);
			var outFilePath = $"{baseDir}{filename}";
			destFs.CreateFile(outFilePath, size, CreateFileOptions.None);
			return destFs.OpenFile(outFilePath, OpenMode.Write);
		}

		public static IFile CreateOrOverwriteFileOpen(IFileSystem destFs, string filename, long size = 0)
		{
			var baseDir = filename.Substring(0, filename.LastIndexOf('/') + 1);
			destFs.EnsureDirectoryExists(baseDir);
			destFs.CreateOrOverwriteFile(filename, 0);
			return destFs.OpenFile(filename, OpenMode.Write);
		}

	}
}