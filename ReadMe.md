# DV Capture Tag
Copies media information from DV encoded files (old-school video camcorders) to the file CreationTime and property tags.

The DV codec includes storing metadata that is not accessible using the standard tag libraries (e.g. TagLib).
This app uses a native MediaInfo library (available on windows and linux) to extract the DV metadata. From there it will parse
the data to extract:
* The original recording date
* The tape name (DV is commonly recorded on digital cassette tapes)
* The timecode In and Out
* Total number of frames
* Frame rate (25 or 30 depending on PAL or NTSC)
* Number of dropped frames - DV commonly captured on circa 1995 hardware via firewire, if the machine
couldn't keep up with the playback speed of the camcorder it would have no choice but to drop frames.

Using the data capture this console app will output the information and optional write the following tags.
* Update the Album tag with the tape name
* Update the Title tag with the tape name
* Update the Comments tag with total and dropped frame information as text

It will most importantly update the CreationTime property on the file to the original recorded date, taking
into account the original timezone.

Consists of two projects, the original DVCaptureTag.Console written against the full .NET framework. This version is deprecated in favour of
its successor DVCaptureTag.Core.Console built against .NET Core 3.1. I was after a command line tool that could be dockerized and run
on the NAS directly where the video files are stored.

## Usage
Use the --help flag to refer to the documentation. This is powerwed by the CommandLineParser library.
```PowerShell
> .\DVCaptureTag.Core.Console --help

DVCaptureTag.Core.Console 0.6
c 2020, Nigel Spencer
  -f, --folderPath             Required.
  -p, --pattern                (Default: *.avi)
  -u, --performUpdate          (Default: false)
  -r, --recurseChildFolders    (Default: false)
  -v, --verbose                (Default: false)
  -q, --quiet                  (Default: false)
  -l, --logLevel               (Default: Error) Sets the log level used by the MediaInfo library.
  -o, --allowTagOverrides      (Default: false) Set to true to allow existing tag values to be overritten.
  --help                       Display this help screen.
  --version                    Display version information.
```

## Containerization
This was also a playground for packaging a container that required additional libraries to be installed. 
You can see this in the Dockerfile where we are building the final image:

```docker
RUN apt-get update
RUN apt-get install -y wget
RUN wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
RUN dpkg -i packages-microsoft-prod.deb
RUN apt-get update
RUN apt-get install -y apt-transport-https
#RUN apt-get update
#RUN apt-get install -y dotnet-sdk-3.1

RUN apt-get update
RUN apt-get install -y libzen0v5 libmms0 libssh-4 libssl1.1 openssl zlib1g zlibc libsqlite3-0 libnghttp2-14 librtmp1 curl
RUN apt-get install -y mediainfo
```

We also need to map a volume so that the container can be configured to access video files.
```docker
VOLUME /app/data
```

Finally we set the entry point, including the paramaters, in this example being just the folder which wish to process.
```docker
ENTRYPOINT ["dotnet", "DVCaptureTag.Core.Console.dll", "--folderPath", "/app/data/SCRATCH_1"]
```
