namespace DVCaptureTag.Console
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using CommandLine;
    using CommandLine.Text;
    using MediaInfo;
    using TagLib.Riff;

    ////using MediaInfo;

    class Program
    {
        private static Regex recordedDateRegex = new Regex(@"^\s*Recorded date\s*:\s(?<RecordedDate>[0-9\- :.]*)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static Regex tapeNameRegex = new Regex(@"^\s*TAPE\s*:\s(?<TapeName>\w*)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static Regex timecodeInRegex = new Regex(@"^\s*TCOD\s*:\s(?<TimeCode>[0-9]*)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static Regex timecodeOutRegex = new Regex(@"^\s*TCDO\s*:\s(?<TimeCode>[0-9]*)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static Regex captureStatsRegex = new Regex(@"^\s*STAT\s*:\s(?<TotalFrames>[0-9]*)\s(?<FramesDropped>[0-9]*)\s(?<Rate>[0-9.]*)\s(?<Other>[0-9]*)\s*", RegexOptions.Multiline | RegexOptions.Compiled);
        private static Regex timecodeFirstRegex = new Regex(@"^Time code of first frame\s*:\s(?<TimeCode>(?<Hour>[0-9]{2}):(?<Minute>[0-9]{2}):(?<Second>[0-9]{2}):(?<Frame>[0-9]*))", RegexOptions.Multiline | RegexOptions.Compiled);
        private static Regex frameRateRegex = new Regex(@"^Frame rate\s*:\s(?<FramesPerSecond>[0-9.]*)\sFPS\s*", RegexOptions.Multiline | RegexOptions.Compiled);

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

            [Option('l', "logLevel", Default = LogLevel.Error, HelpText = "Sets the log level used by the MediaInfo library.")]
            public LogLevel LogLevel { get; set; }

            [Option('o', "allowTagOverrides", Default = false, HelpText = "Set to true to allow existing tag values to be overritten.")]
            public bool AllowTagOverrides { get; set; }
        }

        public struct DVMetadata
        {
            public const double DefaultFrameRatePerSecond = 25.0;
            public const ulong TimecodeFactor = 10000000;

            public DateTime? RecordedDate;
            public string TapeName;
            public ulong? TimecodeIn;
            public ulong? TimecodeOut;
            public uint? CapturedFrames;
            public uint? DroppedFrames;
            public double? FrameRatePerSecond;

            public DateTimeOffset? GetRecordedDateUtc(string path)
            {
                if (!this.RecordedDate.HasValue)
                {
                    return null;
                }

                // Assume Adelaide time
                var fileName = Path.GetFileNameWithoutExtension(path);
                TimeZoneInfo timezone = TimeZoneConverter.TZConvert.GetTimeZoneInfo("Cen. Australia Standard Time");
                if (this.TapeName.StartsWith("CAN") || fileName.StartsWith("CAN"))
                {
                    timezone = TimeZoneConverter.TZConvert.GetTimeZoneInfo("Pacific Standard Time");
                }
                else if (this.TapeName.StartsWith("USA") || fileName.StartsWith("USA"))
                {
                    timezone = TimeZoneConverter.TZConvert.GetTimeZoneInfo("Eastern Standard Time");
                }
                else if (fileName.Contains("Perth"))
                {
                    timezone = TimeZoneConverter.TZConvert.GetTimeZoneInfo("W. Australia Standard Time");
                }
                else if (this.TapeName.Contains("VIC"))
                {
                    timezone = TimeZoneConverter.TZConvert.GetTimeZoneInfo("AUS Eastern Standard Time");
                }
                else if (this.TapeName.Contains("DUNK"))
                {
                    timezone = TimeZoneConverter.TZConvert.GetTimeZoneInfo("E. Australia Standard Time");
                }

                var offset = timezone.GetUtcOffset(this.RecordedDate.Value);
                return new DateTimeOffset(this.RecordedDate.Value, offset);
            }


            public string TimecodeString
            {
                get
                {
                    if (this.TimecodeIn.HasValue && this.TimecodeOut.HasValue)
                    {
                        return $"{TimecodeToString(this.TimecodeIn, this.FrameRatePerSecond)} - {TimecodeToString(this.TimecodeOut, this.FrameRatePerSecond)}";
                    }
                    else if (this.TimecodeIn.HasValue)
                    {
                        return TimecodeToString(this.TimecodeIn, this.FrameRatePerSecond);
                    }

                    return null;
                }
            }

            public static string TimecodeToString(ulong? timecode, double? frameRatePerSecond)
            {
                var frame = (timecode % TimecodeFactor) / (double)TimecodeFactor;
                var time = new TimeSpan(0, 0, (int)(timecode / TimecodeFactor));

                return $"{time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}:{(int)(frame * (frameRatePerSecond ?? DefaultFrameRatePerSecond)) + 1:00}";
            }

            public string Comments
            {
                get
                {
                    string[] parts =
                    {
                        this.TimecodeString,
                        this.DroppedFrames > 0 ? $"({this.DroppedFrames} of {this.CapturedFrames + this.DroppedFrames} dropped frames)" : string.Empty,
                    };
                    return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
                }
            }
        }

        public class Stats
        {
            public uint NumberOfFilesChecked { get; set; }
            public uint NumberOfFileTagsChanged { get; set; }
            public uint NumberOfFilesWithTagChanges { get; set; }
            public uint NumberOfFileCreationDatesChanged { get; set; }
            public uint NumberOfFilesSavedWithTagsUpdated { get; set; }
            public uint NumberOfFilesSaveWithCreationDateUpdated { get; set; }
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

                    var enumerationOptions = new EnumerationOptions()
                    {
                        IgnoreInaccessible = true,
                        MatchType = MatchType.Simple,
                        RecurseSubdirectories = searchOptions == SearchOption.AllDirectories,
                        ReturnSpecialDirectories = false,
                    };

                    Console.WriteLine($"Scanning folder {folderInfo.FullName} {(options.RecurseChildFolders ? "recursively " : "")}for files matching {options.Pattern}:");

                    foreach (var file in folderInfo.GetFiles(options.Pattern, enumerationOptions).OrderBy(fi => fi.FullName))
                    {
                        var path = file.FullName;
                        var metadata = GetDVMetadata(path, options);

                        UpdateViaTagLib(path, metadata, options, stats);
                        UpdateFileCreationTime(path, metadata, options, stats);

                        stats.NumberOfFilesChecked++;
                    }

                    if (!options.Quiet) Console.WriteLine();
                    Console.WriteLine($"Number of matching files found:  {stats.NumberOfFilesChecked}");
                    Console.WriteLine($"Number of file tags changed:     {stats.NumberOfFilesWithTagChanges} files {stats.NumberOfFileTagsChanged} tags");
                    Console.WriteLine($"Number of creation dates set:    {stats.NumberOfFileCreationDatesChanged}");
                    if (!options.PerformUpdate)
                    {
                        Console.WriteLine("** No file updates perfomed: use -u/--performUpdate switch to actually apply file updates.");
                    }
                    else
                    {
                        Console.WriteLine($"Number of files with tags saved: {stats.NumberOfFilesSavedWithTagsUpdated}");
                        Console.WriteLine($"Number of files with date saved: {stats.NumberOfFilesSaveWithCreationDateUpdated}");
                    }

                    if (options.WaitOnEnter)
                    {
                        Console.ReadLine();
                    }
                });
        }

        private static DVMetadata GetDVMetadata(string path, Options options)
        {
            if (!options.Quiet)
            {
                Console.WriteLine(path);
            }

            // This first line is used just to ensure we have the MediaInfo library loaded.
            var logger = new Logger(options.LogLevel);
            var mediaInfo = new MediaInfoWrapper(path, logger);

            var filePtr = new MediaInfo();
            filePtr.Open(path);

            try
            {
                var result = filePtr.Inform();
                if (!options.Quiet && options.LogLevel <= LogLevel.Verbose)
                {
                    Console.WriteLine($"  {result}");
                }

                var recordedDateMatch = recordedDateRegex.Match(result);
                var tapeNameMatch = tapeNameRegex.Match(result);
                var timecodeInMatch = timecodeInRegex.Match(result);
                var timecodeOutMatch = timecodeOutRegex.Match(result);
                var captureStatsMatch = captureStatsRegex.Match(result);
                var timecodeFirstMatch = timecodeFirstRegex.Match(result);
                var frameRateMatch = frameRateRegex.Match(result);

                DVMetadata output = new DVMetadata();
                if (DateTime.TryParse(recordedDateMatch.Groups["RecordedDate"]?.Value, out DateTime recordedDate)) output.RecordedDate = recordedDate;
                output.TapeName = tapeNameMatch.Groups["TapeName"]?.Value;
                if (double.TryParse(frameRateMatch.Groups["FramesPerSecond"].Value, out double frameRatePerSecond)) output.FrameRatePerSecond = frameRatePerSecond;
                if (ulong.TryParse(timecodeInMatch.Groups["TimeCode"]?.Value, out ulong timecodeIn))
                {
                    output.TimecodeIn = timecodeIn;
                }
                else
                {
                    if (timecodeFirstMatch.Success)
                    {
                        output.TimecodeIn = 
                            (ulong.Parse(timecodeFirstMatch.Groups["Hour"].Value) * 60 * 60 * DVMetadata.TimecodeFactor) +
                            (ulong.Parse(timecodeFirstMatch.Groups["Minute"].Value) * 60 * DVMetadata.TimecodeFactor) +
                            (ulong.Parse(timecodeFirstMatch.Groups["Second"].Value) * DVMetadata.TimecodeFactor) + 
                            (ulong)(double.Parse(timecodeFirstMatch.Groups["Frame"].Value) / (output.FrameRatePerSecond ?? DVMetadata.DefaultFrameRatePerSecond) * DVMetadata.TimecodeFactor);
                    }
                }
                if (ulong.TryParse(timecodeOutMatch.Groups["TimeCode"]?.Value, out ulong timecodeOut)) output.TimecodeOut = timecodeOut;
                if (uint.TryParse(captureStatsMatch.Groups["TotalFrames"]?.Value, out uint totalFrames)) output.CapturedFrames = totalFrames;
                if (uint.TryParse(captureStatsMatch.Groups["FramesDropped"]?.Value, out uint droppedFrames)) output.DroppedFrames = droppedFrames;

                if (!options.Quiet && options.Verbose)
                {
                    Console.Write($"    Recorded Date:   {output.RecordedDate}");
                    Console.Write($", {output.GetRecordedDateUtc(path)}");
                    Console.WriteLine($", {output.GetRecordedDateUtc(path)?.UtcDateTime}");
                    Console.WriteLine($"    Tape Name:       {output.TapeName}");
                    Console.WriteLine($"    Timecode In:     {output.TimecodeIn}");
                    Console.WriteLine($"    Timecode Out:    {output.TimecodeOut}");
                    Console.WriteLine($"    Total Frames:    {output.CapturedFrames}");
                    Console.WriteLine($"    Frame rate:      {output.FrameRatePerSecond} FPS");
                    Console.WriteLine($"    Dropped Frames:  {output.DroppedFrames}");
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
                if (osFile.CreationTimeUtc != metadata.GetRecordedDateUtc(path))
                {
                    if (!options.Quiet)
                    {
                        Console.WriteLine($"  CreationTime updated from {osFile.CreationTime} to match Recorded Date: {metadata.RecordedDate.Value}");
                        Console.WriteLine($"  CreationTimeUtc updated from {osFile.CreationTimeUtc} to match Recorded Date: {metadata.GetRecordedDateUtc(path).Value}");
                    }
                    if (options.PerformUpdate)
                    {
                        ////osFile.CreationTime = metadata.RecordedDate.Value;
                        osFile.CreationTimeUtc = metadata.GetRecordedDateUtc(path).Value.UtcDateTime;
                        stats.NumberOfFilesSaveWithCreationDateUpdated++;
                    }
                    stats.NumberOfFileCreationDatesChanged++;
                }
            }
        }

        private static void UpdateViaTagLib(string path, DVMetadata metadata, Options options, Stats stats)
        {
            using (var tagFile = TagLib.File.Create(path))
            {
                var tagsUpdated = false;

                if (!tagFile.Writeable && !options.Quiet)
                {
                    Console.WriteLine($"  Tags are not writable for this file.");
                }

                if (metadata.TapeName != null)
                {
                    if ((tagFile.Tag.Album ?? string.Empty) != (metadata.TapeName ?? string.Empty))
                    {
                        if (!options.AllowTagOverrides && !string.IsNullOrWhiteSpace(tagFile.Tag.Album))
                        {
                            Console.WriteLine($"  Album tag will not be updated from '{tagFile.Tag.Album}' to '{metadata.TapeName}', use --allowTagOverrides flag to override existing tag values.");
                        }
                        else
                        {
                            if (!options.Quiet) Console.WriteLine($"  Album tag updated from '{tagFile.Tag.Album}' to match tape name: '{metadata.TapeName}'");
                            tagFile.Tag.Album = metadata.TapeName;
                            stats.NumberOfFileTagsChanged++;
                            tagsUpdated = true;
                        }
                    }

                    if ((tagFile.Tag.Title ?? string.Empty) != (metadata.TapeName ?? string.Empty))
                    {
                        if (!options.AllowTagOverrides && !string.IsNullOrWhiteSpace(tagFile.Tag.Title))
                        {
                            Console.WriteLine($"  Title tag will not be updated from '{tagFile.Tag.Title}' to '{metadata.TapeName}', use --allowTagOverrides flag to override existing tag values.");
                        }
                        else
                        {
                            if (!options.Quiet) Console.WriteLine($"  Title tag updated from '{tagFile.Tag.Title}' to match tape name: '{metadata.TapeName}'");
                            tagFile.Tag.Title = metadata.TapeName;
                            stats.NumberOfFileTagsChanged++;
                            tagsUpdated = true;
                        }
                    }
                }

                if (metadata.Comments != null &&
                    tagFile.Tag.Comment != metadata.Comments)
                {
                    if (!options.AllowTagOverrides && !string.IsNullOrWhiteSpace(tagFile.Tag.Comment))
                    {
                        Console.WriteLine($"  Comment tag will not be updated from '{tagFile.Tag.Comment}' to '{metadata.Comments}', use --allowTagOverrides flag to override existing tag values.");
                    }
                    else
                    {
                        if (!options.Quiet) Console.WriteLine($"  Comment tag updated from '{tagFile.Tag.Comment}' to include timecode information: '{metadata.Comments}'");
                        tagFile.Tag.Comment = metadata.Comments;
                        stats.NumberOfFileTagsChanged++;
                        tagsUpdated = true;
                    }
                }

                if (tagsUpdated)
                {
                    stats.NumberOfFilesWithTagChanges++;

                    if (options.PerformUpdate && tagFile.Writeable)
                    {
                        tagFile.Save();
                        Console.WriteLine("  Tags saved.");
                        stats.NumberOfFilesSavedWithTagsUpdated++;
                    }
                }
            }
        }

        public class Logger : ILogger
        {
            public Logger(LogLevel logLevel)
            {
                this.LogLevel = logLevel;
            }

            public LogLevel LogLevel { get; set; }

            public void Log(LogLevel loglevel, string message, params object[] parameters)
            {
                if (loglevel >= this.LogLevel)
                {
                    Console.WriteLine($"  {loglevel}: {message}", parameters);
                }
            }
        }
    }
}
