﻿
/* Copyright (C) 2022 Chosen Few Software
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

using CommandLine;
using DokanNet;
using QuesoStruct;

using System;
using System.Collections.Generic;
using System.Text;

namespace DATOneArchiver.DokanDriver
{
    class Program
    {
        private static readonly Dictionary<string, Endianess> endianesses =
            new Dictionary<string, Endianess>
            {
                { "little", Endianess.Little },
                { "big", Endianess.Big },
            };

        private static readonly Dictionary<string, Game> games =
            new Dictionary<string, Game>
            {
                { "lsw1", Game.LSW1 },
                { "lsw2", Game.LSW2 },
            };
        private class MountOptions
        {
            [Option('f', "archive-file", Required = true, HelpText = "The DAT archive to operate on.")]
            public string ArchivePath { get; set; }

            [Option('e', "endian", Default = "little", HelpText = "The endianess of the archive file.")]
            public string Endianess { get; set; }

            [Option('g', "game", Required = true, HelpText = "The game of origin for the input file. Can be lsw1 or lsw2.")]
            public string Game { get; set; }

            [Option('a', "align", Default = 1, HelpText = "The alignment to enforce for embedded file data. Setting to 1 disables any alignment control.")]
            public int FileAlign { get; set; }

            [Option('m', "mount-point", Required = true, HelpText = "Mount point for the DAT archive's contents")]
            public string MountPoint { get; set; }
        }

        private static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.Unicode;
            Parser.Default.ParseArguments<MountOptions>(args)
                .WithParsed(RunMount);
        }

        private static void RunMount(MountOptions options)
        {
            Dokan.Init();

            var archive = new Archive(
                options.ArchivePath, ArchiveMode.ReadWrite, 
                games[options.Game.ToLowerInvariant()], 
                endianesses[options.Endianess.ToLowerInvariant()], 
                options.FileAlign
                );

            archive.Read();

            new ArchiveOperations(archive).Mount(options.MountPoint, DokanOptions.RemovableDrive, true);

            Dokan.Shutdown();
        }
    }
}