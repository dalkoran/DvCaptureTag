namespace DVCaptureTag.Console
{
    using System;
    using System.IO;
    using System.Text.RegularExpressions;
    using CommandLine;
    using MediaInfo;

    ////using MediaInfo;

    class Program
    {
        public class Options
        {
            [Option('f', "folderPath", Required = true)]
            public string FolderPath { get; set; }

            [Option('p', "pattern", Required = false, Default = "*.avi")]
            public string Pattern { get; set; }

            [Option('u', "performUpdate", Required = false, Default = false)]
            public bool PerformUpdate { get; set; }

            [Option('r', "recurseChildFolders", Required = false, Default = false)]
            public bool RecurseChildFolders { get; set; }

            [Option('v', "verbose", Required = false, Default = false)]
            public bool Verbose { get; set; }

            [Option('q', "quiet", Required = false, Default = false)]
            public bool Quiet { get; set; }

            [Option('w', "waitOnEnter", Default = false, HelpText = "The command pauses prior to completion awaiting input from the user.", Hidden = true)]
            public bool WaitOnEnter { get; set; }
        }

        public struct DVMetadata
        {
            public DateTime? RecordedDate;
            public string TapeName;
            public ulong? TimecodeIn;
            public ulong? TimecodeOut;
            public uint? TotalFrames;
            public uint? DroppedFrames;

            public string TimecodeString
            {
                get
                {
                    if (this.TimecodeIn.HasValue && this.TimecodeOut.HasValue)
                    {
                        return $"{TimecodeToString(this.TimecodeIn)} - {TimecodeToString(this.TimecodeOut)}";
                    }

                    return null;
                }
            }

            public static string TimecodeToString(ulong? timecode)
            {
                var frame = (timecode % 10000000) / 10000000.0;
                var time = new TimeSpan(0, 0, (int)(timecode / 10000000));

                return $"{time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}:{(int)(frame * 25) + 1:00}";
            }
        }

        public class Stats
        {
            public uint NumberOfFilesProcessed { get; set; }
            public uint NumberOfFileTagsUpdated { get; set; }
            public uint NumberOfFileDatesUpdated { get; set; }
        }

        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(options =>
                {
                    var folderInfo = new DirectoryInfo(options.FolderPath);
                    var stats = new Stats();

                    var searchOptions = options.RecurseChildFolders
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly;

                    Console.WriteLine($"Scanning folder {folderInfo.FullName} {(options.RecurseChildFolders ? "recursively " : "")}for files matching {options.Pattern}:");

                    foreach (var file in folderInfo.GetFiles(options.Pattern, searchOptions))
                    {
                        var path = file.FullName;
                        var metadata = GetDVMetadata(path, options);

                        UpdateViaTagLib(path, metadata, options, stats);
                        UpdateFileCreationTime(path, metadata, options, stats);

                        stats.NumberOfFilesProcessed++;
                    }

                    if (!options.Quiet) Console.WriteLine();
                    Console.WriteLine($"Number of matching files found: {stats.NumberOfFilesProcessed}");
                    Console.WriteLine($"Number of file tags updated:    {stats.NumberOfFileTagsUpdated}");
                    Console.WriteLine($"Number of creation dates set:   {stats.NumberOfFileDatesUpdated}");

                    if (options.WaitOnEnter)
                    {
                        Console.ReadLine();
                    }
                });
        }

        private static DVMetadata GetDVMetadata(string path, Options options)
        {
            // This first line is used just to ensure we have the MediaInfo library loaded.
            var logger = new Logger();
            var mediaInfo = new MediaInfoWrapper(path, logger);

            var filePtr = new MediaInfo();
            filePtr.Open(path);

            try
            {
                var result = filePtr.Inform();

                var recordedDateMatch = Regex.Match(result, @"^\s*Recorded date\s*:\s(?<RecordedDate>[0-9\- :.]*)", RegexOptions.Multiline);
                var tapeNameMatch = Regex.Match(result, @"^\s*TAPE\s*:\s(?<TapeName>\w*)", RegexOptions.Multiline);
                var timecodeInMatch = Regex.Match(result, @"^\s*TCOD\s*:\s(?<TimeCode>[0-9]*)", RegexOptions.Multiline);
                var timecodeOutMatch = Regex.Match(result, @"^\s*TCDO\s*:\s(?<TimeCode>[0-9]*)", RegexOptions.Multiline);
                var captureStatsMatch = Regex.Match(result, @"^\s*STAT\s*:\s(?<TotalFrames>[0-9]*)\s(?<FramesDropped>[0-9]*)\s(?<Rate>[0-9.]*)\s(?<Other>[0-9]*)\s*", RegexOptions.Multiline);

                DVMetadata output = new DVMetadata();
                if (DateTime.TryParse(recordedDateMatch.Groups["RecordedDate"]?.Value, out DateTime recordedDate)) output.RecordedDate = recordedDate;
                output.TapeName = tapeNameMatch.Groups["TapeName"]?.Value;
                if (ulong.TryParse(timecodeInMatch.Groups["TimeCode"]?.Value, out ulong timecodeIn)) output.TimecodeIn = timecodeIn;
                if (ulong.TryParse(timecodeOutMatch.Groups["TimeCode"]?.Value, out ulong timecodeOut)) output.TimecodeOut = timecodeOut;
                if (uint.TryParse(captureStatsMatch.Groups["TotalFrames"]?.Value, out uint totalFrames)) output.TotalFrames = totalFrames;
                if (uint.TryParse(captureStatsMatch.Groups["FramesDropped"]?.Value, out uint droppedFrames)) output.DroppedFrames = droppedFrames;

                if (!options.Quiet)
                {
                    Console.WriteLine(path);
                    if (options.Verbose)
                    {
                        Console.WriteLine($"    Recorded Date:   {output.RecordedDate}");
                        Console.WriteLine($"    Tape Name:       {output.TapeName}");
                        Console.WriteLine($"    Timecode In:     {output.TimecodeIn}");
                        Console.WriteLine($"    Timecode Out:    {output.TimecodeOut}");
                        Console.WriteLine($"    Total Frames:    {output.TotalFrames}");
                        Console.WriteLine($"    Dropped Frames:  {output.DroppedFrames}");
                    }
                }

                return output;
            }
            finally
            {
                filePtr.Close();
            }
        }

        private static void UpdateFileCreationTime(string path, DVMetadata metadata, Options options, Stats stats)
        {
            if (metadata.RecordedDate.HasValue)
            {
                var osFile = new FileInfo(path);
                if (osFile.CreationTime != metadata.RecordedDate)
                {
                    if (!options.Quiet)
                    {
                        Console.WriteLine($"  CreationTime updated from {osFile.CreationTime} to match Recorded Date: {metadata.RecordedDate.Value}");
                    }
                    if (options.PerformUpdate)
                    {
                        osFile.CreationTime = metadata.RecordedDate.Value;
                    }
                    stats.NumberOfFileDatesUpdated++;
                    osFile.Refresh();
                }
            }
        }

        private static void UpdateViaTagLib(string path, DVMetadata metadata, Options options, Stats stats)
        {
            using (var tagFile = TagLib.File.Create(path))
            {
                if (metadata.TapeName != null && tagFile.Tag.Title != metadata.TapeName)
                {
                    tagFile.Tag.Title = metadata.TapeName;
                    stats.NumberOfFileTagsUpdated++;
                    if (!options.Quiet) Console.WriteLine($"  Title tag updated to match Tape Name: {metadata.TapeName}");
                }

                if (metadata.TimecodeString != null && tagFile.Tag.Comment != metadata.TimecodeString)
                {
                    tagFile.Tag.Comment = metadata.TimecodeString;
                    stats.NumberOfFileTagsUpdated++;
                    if (!options.Quiet) Console.WriteLine("  Comment tag updated to include Timecode information.");
                }

                if (options.PerformUpdate)
                {
                    tagFile.Save();
                    Console.WriteLine("  Tags saved.");
                }
            }
        }

        public class Logger : ILogger
        {
            public void Log(LogLevel loglevel, string message, params object[] parameters)
            {
                ////Console.WriteLine($"{loglevel}: {message}", parameters);
            }
        }
    }
}
