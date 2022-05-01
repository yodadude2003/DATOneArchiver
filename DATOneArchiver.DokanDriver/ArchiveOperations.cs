﻿/* Copyright (C) 2022 Chosen Few Software
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using DokanNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using FileAccess = DokanNet.FileAccess;

namespace DATOneArchiver.DokanDriver
{
    internal class ArchiveOperations : IDokanOperations
    {
        private const FileAccess DataAccess = FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData |
                                              FileAccess.Execute |
                                              FileAccess.GenericExecute | FileAccess.GenericWrite |
                                              FileAccess.GenericRead;

        private readonly Archive archive;
        private readonly Dictionary<string, FileStream> virt;

        public ArchiveOperations(Archive archive)
        {
            this.archive = archive;
            virt = new Dictionary<string, FileStream>();
        }

        private string GetVirtPath(string fileName)
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archive.FilePath)), "_virt" + fileName);
        }

        public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode,
            FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        {
            var result = DokanResult.Success;
            var filePath = fileName.ToLowerInvariant();

            if (info.IsDirectory)
            {
                switch (mode)
                {
                    case FileMode.Open:
                        if (archive.Root.Get(filePath, false)?.Children == null)
                        {
                            if (archive.Root.Get(filePath, false) == null)
                            {
                                return DokanResult.PathNotFound;
                            }
                            else
                            {
                                return DokanResult.NotADirectory;
                            }
                        }

                        break;

                    case FileMode.CreateNew:
                        if (archive.Root.Get(filePath, false) != null)
                            return DokanResult.FileExists;

                        archive.Root.Get(filePath, true, true);
                        break;
                }
            }
            else
            {
                var node = archive.Root.Get(filePath, false);

                var pathExists = node != null;
                var pathIsDirectory = pathExists ? node.Children != null : false;

                switch (mode)
                {
                    case FileMode.Open:

                        if (pathExists)
                        {
                            // check if driver only wants to read attributes, security info, or open directory
                            if (pathIsDirectory)
                            {
                                if ((access & FileAccess.Delete) == FileAccess.Delete
                                    && (access & FileAccess.Synchronize) != FileAccess.Synchronize)
                                    //It is a DeleteFile request on a directory
                                    return DokanResult.AccessDenied;

                                info.IsDirectory = true;
                                info.Context = new object();
                                // must set it to something if you return DokanError.Success

                                return DokanResult.Success;
                            }
                        }
                        else
                        {
                            return DokanResult.FileNotFound;
                        }
                        break;

                    case FileMode.CreateNew:
                        if (pathExists)
                            return DokanResult.FileExists;
                        break;

                    case FileMode.Truncate:
                        if (!pathExists)
                            return DokanResult.FileNotFound;
                        break;
                }

                try
                {
                    if (pathExists)
                    {
                        if (access.HasFlag(FileAccess.Delete) && options.HasFlag(FileOptions.DeleteOnClose))
                        {
                            node.ShouldDelete = true;
                        }
                        else if (mode == FileMode.OpenOrCreate || mode == FileMode.Create)
                        {
                            result = DokanResult.AlreadyExists;
                        }
                    }
                    else
                    {
                        node = archive.Root.Get(filePath);
                    }

                    if (virt.ContainsKey(filePath)) 
                    {
                        info.Context = virt[filePath];
                    }
                    else 
                    {
                        var path = GetVirtPath(filePath);
                        var dir = Path.GetDirectoryName(path);

                        if (!Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        var stream = File.Create(GetVirtPath(filePath));

                        node.Stream?.CopyTo(stream);
                        node.Stream?.Seek(0, SeekOrigin.Begin);

                        virt.Add(filePath, stream);
                        node.Stream = stream;

                        info.Context = stream;
                    }
                }
                catch (IOException)
                {
                    result = DokanResult.Unsuccessful;
                }
            }
            return result;
        }

        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            var filePath = fileName.ToLowerInvariant();
            var parent = archive.Root.Get(Path.GetDirectoryName(filePath), false);
            if (parent == null)
            {
                return DokanResult.PathNotFound;
            }

            var node = archive.Root.Get(fileName, false);
            if (node == null)
            {
                return DokanResult.FileNotFound;
            }

            if (virt.ContainsKey(filePath))
            {
                virt[filePath].Dispose();
                virt.Remove(filePath);

                File.Delete(GetVirtPath(filePath));
            }

            parent.Children.Remove(Path.GetFileName(filePath));

            return DokanResult.Success;
        }

        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            var filePath = fileName.ToLowerInvariant();
            var parent = archive.Root.Get(Path.GetDirectoryName(filePath), false);
            var node = archive.Root.Get(filePath, false);

            if (parent == null || node == null)
            {
                return DokanResult.PathNotFound;
            }
            else if (node.Children.Any())
            {
                return DokanResult.DirectoryNotEmpty;
            }

            parent.Children.Remove(Path.GetFileName(filePath));

            return DokanResult.Success;
        }
        public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
        {
            var oldPath = oldName.ToLowerInvariant();
            var newPath = newName.ToLowerInvariant();

            var node = archive.Root.Get(oldPath, false);

            var oldParent = archive.Root.Get(Path.GetDirectoryName(oldPath), false);
            var newParent = archive.Root.Get(Path.GetDirectoryName(newPath), false);

            if (node == null || oldParent == null || newParent == null)
            {
                return DokanResult.PathNotFound;
            }
            else if (!newParent.Children.ContainsKey(Path.GetFileName(newPath)) || replace)
            {
                oldParent.Children.Remove(Path.GetFileName(oldPath));
                newParent.Children.Remove(Path.GetFileName(newPath));

                virt[oldPath].Dispose();
                virt.Remove(oldPath);

                File.Move(GetVirtPath(oldPath), GetVirtPath(newPath), replace);

                var stream = File.Open(GetVirtPath(newPath), FileMode.Open, System.IO.FileAccess.ReadWrite);
                virt.Add(newPath, stream);

                archive.Root.Get(newPath, true, node.Children != null).Stream = stream;

                return DokanResult.Success;
            }
            else
            {
                return DokanResult.AlreadyExists;
            }
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        {
            var filePath = fileName.ToLowerInvariant();
            var node = archive.Root.Get(filePath, false);
            if (node == null)
            {
                files = null;
                return DokanResult.PathNotFound;
            }
            else if (node.Children == null)
            {
                files = null;
                return DokanResult.NotADirectory;
            }

            files = node.Children.Values
                .Select(n => new FileInformation
                {
                    Attributes = n.Children != null ? FileAttributes.Directory : FileAttributes.Normal,
                    FileName = n.Name,
                    Length = n.Stream?.Length ?? 0,
                }).ToList();

            return DokanResult.Success;
        }

        public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
        {
            files = null;
            return DokanResult.NotImplemented;
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
        {
            streams = null;
            return DokanResult.NotImplemented;
        }

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
        {
            var filePath = fileName.ToLowerInvariant();
            var node = archive.Root.Get(filePath, false);
            if (node == null)
            {
                fileInfo = default(FileInformation);
                return DokanResult.FileNotFound;
            }

            fileInfo = new FileInformation
            {
                Attributes = node.Children != null ? FileAttributes.Directory : FileAttributes.Normal,
                FileName = node.Name,
                Length = node.Stream?.Length ?? 0,
            };

            return DokanResult.Success;
        }

        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        {
            security = null;
            return DokanResult.NotImplemented;
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
        {
            volumeLabel = Path.GetFileNameWithoutExtension(archive.FilePath).ToUpper();
            features = FileSystemFeatures.None;
            fileSystemName = "TT-DAT";
            maximumComponentLength = uint.MaxValue;

            return DokanResult.Success;
        }

        public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
        {
            freeBytesAvailable = 0;
            totalNumberOfBytes = 0;
            totalNumberOfFreeBytes = 0;
            return DokanResult.NotImplemented;
        }

        public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
        {
            return DokanResult.Success;
        }

        public NtStatus Unmounted(IDokanFileInfo info)
        {
            try
            {
                var oldArchivePath = archive.FilePath;
                var newArchivePath = Path.ChangeExtension(archive.FilePath, "tmp");

                var newArchive = new Archive(newArchivePath, ArchiveMode.BuildNew, archive.Game, archive.Endianess, archive.FileAlign);
                newArchive.Root = archive.Root;
                newArchive.Rebuild();

                foreach (var stream in virt.Values)
                {
                    stream.Dispose();
                }

                archive.Dispose();
                newArchive.Dispose();

                Directory.Delete(GetVirtPath("\\"), true);
                File.Move(newArchivePath, oldArchivePath, true);

                return DokanResult.Success;
            }
            catch (Exception)
            {
                return DokanResult.Unsuccessful;
            }
        }

        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
        {
            var filePath = fileName.ToLowerInvariant();
            var node = archive.Root.Get(filePath, false);
            if (node == null)
            {
                bytesRead = 0;
                return DokanResult.Unsuccessful;
            }

            try
            {
                node.Stream.Seek(offset, SeekOrigin.Begin);
                bytesRead = node.Stream.Read(buffer, 0, buffer.Length);

                return DokanResult.Success;
            }
            catch (IOException)
            {
                bytesRead = 0;
                return DokanResult.Unsuccessful;
            }
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
        {
            var filePath = fileName.ToLowerInvariant();
            var node = archive.Root.Get(filePath, false);
            if (node == null)
            {
                bytesWritten = 0;
                return DokanResult.Unsuccessful;
            }

            try
            {
                node.Stream.Seek(offset, SeekOrigin.Begin);

                node.Stream.Write(buffer, 0, buffer.Length);
                bytesWritten = buffer.Length;

                return DokanResult.Success;
            }
            catch (IOException)
            {
                bytesWritten = 0;
                return DokanResult.Unsuccessful;
            }
        }

        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            var filePath = fileName.ToLowerInvariant();
            var node = archive.Root.Get(filePath, false);
            if (node == null)
            {
                return DokanResult.Unsuccessful;
            }

            try
            {
                node.Stream.Flush();
                return DokanResult.Success;
            }
            catch (IOException)
            {
                return DokanResult.Unsuccessful;
            }
        }

        public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        {
            var filePath = fileName.ToLowerInvariant();
            var node = archive.Root.Get(filePath, false);
            if (node == null)
            {
                return DokanResult.Unsuccessful;
            }

            try
            {
                node.Stream.SetLength(length);
                return DokanResult.Success;
            }
            catch (IOException)
            {
                return DokanResult.Unsuccessful;
            }
        }

        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            return SetEndOfFile(fileName, length, info);
        }

        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        public void CloseFile(string fileName, IDokanFileInfo info)
        {
            var filePath = fileName.ToLowerInvariant();
            if (archive.Root.Get(filePath, false)?.ShouldDelete == true)
            {
                DeleteFile(fileName, info);
            }
        }

        public void Cleanup(string fileName, IDokanFileInfo info)
        {
            info.Context = null;
            return;
        }
    }
}