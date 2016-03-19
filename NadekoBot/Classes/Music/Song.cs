﻿using Discord.Audio;
using NadekoBot.Extensions;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoLibrary;

namespace NadekoBot.Classes.Music {
    public class SongInfo {
        public string Provider { get; internal set; }
        public MusicType ProviderType { get; internal set; }
        public string Title { get; internal set; }
        public string Uri { get; internal set; }
    }
    /// <summary>
    /// 💩
    /// </summary>
    public class PoopyBuffer {

        private readonly byte[] ringBuffer;

        public int WritePosition { get; private set; } = 0;
        public int ReadPosition { get; private set; } = 0;

        public int ContentLength => (WritePosition >= ReadPosition ?
                                     WritePosition - ReadPosition :
                                     (BufferSize - ReadPosition) + WritePosition);

        public int BufferSize { get; }

        private readonly object readWriteLock = new object();

        public PoopyBuffer(int size) {
            if (size <= 0)
                throw new ArgumentException();
            BufferSize = size;
            ringBuffer = new byte[size];
        }

        public int Read(byte[] buffer, int count) {
            if (buffer.Length < count)
                throw new ArgumentException();
            //Console.WriteLine($"***\nRead: {ReadPosition}\nWrite: {WritePosition}\nContentLength:{ContentLength}\n***");
            lock (readWriteLock) {
                //read as much as you can if you're reading too much
                if (count > ContentLength)
                    count = ContentLength;
                //if nothing to read, return 0
                if (WritePosition == ReadPosition)
                    return 0;
                // if buffer is in the "normal" state, just read
                if (WritePosition > ReadPosition) {
                    Buffer.BlockCopy(ringBuffer, ReadPosition, buffer, 0, count);
                    ReadPosition += count;
                    //Console.WriteLine($"Read only normally1 {count}[{ReadPosition - count} to {ReadPosition}]");
                    return count;
                }
                //else ReadPos <Writepos
                // buffer is in its inverted state
                // A: if i can read as much as possible without hitting the buffer.length, read that

                if (count + ReadPosition <= BufferSize) {
                    Buffer.BlockCopy(ringBuffer, ReadPosition, buffer, 0, count);
                    ReadPosition += count;
                    //Console.WriteLine($"Read only normally2 {count}[{ReadPosition - count} to {ReadPosition}]");
                    return count;
                }
                // B: if i can't read as much, read to the end,
                var readNormaly = BufferSize - ReadPosition;
                Buffer.BlockCopy(ringBuffer, ReadPosition, buffer, 0, readNormaly);

                //Console.WriteLine($"Read normaly {count}[{ReadPosition} to {ReadPosition + readNormaly}]");
                //then read the remaining amount from the start

                var readFromStart = count - readNormaly;
                Buffer.BlockCopy(ringBuffer, 0, buffer, readNormaly, readFromStart);
                //Console.WriteLine($"Read From start {readFromStart}[{0} to {readFromStart}]");
                ReadPosition = readFromStart;
                return count;
            }
        }

        public async Task WriteAsync(byte[] buffer, int count, CancellationToken cancelToken) {
            if (count > buffer.Length)
                throw new ArgumentException();
            while (ContentLength + count > BufferSize) {
                await Task.Delay(20, cancelToken);
                if (cancelToken.IsCancellationRequested)
                    return;
            }
            //the while above assures that i cannot write past readposition with my write, so i don't have to check
            // *unless its multithreaded or task is not awaited
            lock (readWriteLock) {
                // if i can just write without hitting buffer.length, do it
                if (WritePosition + count < BufferSize) {
                    Buffer.BlockCopy(buffer, 0, ringBuffer, WritePosition, count);
                    WritePosition += count;
                    //Console.WriteLine($"Wrote only normally {count}[{WritePosition - count} to {WritePosition}]");
                    return;
                }
                // otherwise, i have to write to the end, then write the rest from the start

                var wroteNormaly = BufferSize - WritePosition;
                Buffer.BlockCopy(buffer, 0, ringBuffer, WritePosition, wroteNormaly);

                //Console.WriteLine($"Wrote normally {wroteNormaly}[{WritePosition} to {BufferSize}]");

                var wroteFromStart = count - wroteNormaly;
                Buffer.BlockCopy(buffer, wroteNormaly, ringBuffer, 0, wroteFromStart);

                //Console.WriteLine($"and from start {wroteFromStart} [0 to {wroteFromStart}");

                WritePosition = wroteFromStart;
            }
        }
    }
    public class Song {
        public StreamState State { get; internal set; }
        public string PrettyName =>
            $"**【 {SongInfo.Title.TrimTo(55)} 】**`{(SongInfo.Provider ?? "-")}`";
        public SongInfo SongInfo { get; }

        private PoopyBuffer songBuffer { get; } = new PoopyBuffer(4.MiB());

        private bool prebufferingComplete { get; set; } = false;
        public MusicPlayer MusicPlayer { get; set; }

        public string PrettyCurrentTime() {
            var time = TimeSpan.FromSeconds(bytesSent / 3840 / 50);
            return $"【{(int)time.TotalMinutes}m {time.Seconds}s】";
        }

        private ulong bytesSent { get; set; } = 0;

        private Song(SongInfo songInfo) {
            this.SongInfo = songInfo;
        }

        private Task BufferSong(CancellationToken cancelToken) =>
            Task.Run(async () => {
                Process p = null;
                try {
                    p = Process.Start(new ProcessStartInfo {
                        FileName = "ffmpeg",
                        Arguments = $"-i {SongInfo.Uri} -f s16le -ar 48000 -ac 2 pipe:1 -loglevel quiet",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = false,
                        CreateNoWindow = true,
                    });
                    const int blockSize = 3840;
                    var buffer = new byte[blockSize];
                    var attempt = 0;
                    while (!cancelToken.IsCancellationRequested) {
                        var read = 0;
                        try {
                            read = await p.StandardOutput.BaseStream.ReadAsync(buffer, 0, blockSize, cancelToken);
                        } catch {
                            return;
                        }
                        if (read == 0)
                            if (attempt++ == 50)
                                break;
                            else
                                await Task.Delay(100, cancelToken);
                        else {
                            attempt = 0;
                            await Task.Delay(5, cancelToken);
                        }
                        await songBuffer.WriteAsync(buffer, read, cancelToken);
                        if (songBuffer.ContentLength > 2.MB())
                            prebufferingComplete = true;
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"Buffering errored: {ex.Message}");
                } finally {
                    Console.WriteLine($"Buffering done." + $" [{songBuffer.ContentLength}]");
                    if (p != null) {
                        try {
                            p.Kill();
                        } catch { }
                        p.Dispose();
                    }
                }
            });

        internal async Task Play(IAudioClient voiceClient, CancellationToken cancelToken) {
            var bufferTask = new ConfiguredTaskAwaitable();
            try {
                bufferTask = BufferSong(cancelToken).ConfigureAwait(false);
            } catch (Exception ex) {
                var clr = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERR BUFFER START : {ex.Message}\n{ex}");
                Console.ForegroundColor = clr;
            }
            var bufferAttempts = 0;
            const int waitPerAttempt = 500;
            var toAttemptTimes = SongInfo.ProviderType != MusicType.Normal ? 5 : 9;
            while (!prebufferingComplete && bufferAttempts++ < toAttemptTimes) {
                await Task.Delay(waitPerAttempt, cancelToken);
            }
            cancelToken.ThrowIfCancellationRequested();
            Console.WriteLine($"Prebuffering done? in {waitPerAttempt * bufferAttempts}");
            const int blockSize = 3840;
            var attempt = 0;
            while (!cancelToken.IsCancellationRequested) {
                //Console.WriteLine($"Read: {songBuffer.ReadPosition}\nWrite: {songBuffer.WritePosition}\nContentLength:{songBuffer.ContentLength}\n---------");
                byte[] buffer = new byte[blockSize];
                var read = songBuffer.Read(buffer, blockSize);
                unchecked {
                    bytesSent += (ulong)read;
                }
                if (read == 0)
                    if (attempt++ == 20) {
                        voiceClient.Wait();
                        Console.WriteLine($"Song finished. [{songBuffer.ContentLength}]");
                        break;
                    } else
                        await Task.Delay(100, cancelToken);
                else
                    attempt = 0;

                while (this.MusicPlayer.Paused)
                    await Task.Delay(200, cancelToken);
                buffer = AdjustVolume(buffer, MusicPlayer.Volume);
                voiceClient.Send(buffer, 0, read);
            }
            Console.WriteLine("Awiting buffer task");
            await bufferTask;
            Console.WriteLine("Buffer task done.");
            voiceClient.Clear();
            cancelToken.ThrowIfCancellationRequested();
        }

        //stackoverflow ftw
        private static byte[] AdjustVolume(byte[] audioSamples, float volume) {
            if (Math.Abs(volume - 1.0f) < 0.01f)
                return audioSamples;
            var array = new byte[audioSamples.Length];
            for (var i = 0; i < array.Length; i += 2) {

                // convert byte pair to int
                short buf1 = audioSamples[i + 1];
                short buf2 = audioSamples[i];

                buf1 = (short)((buf1 & 0xff) << 8);
                buf2 = (short)(buf2 & 0xff);

                var res = (short)(buf1 | buf2);
                res = (short)(res * volume);

                // convert back
                array[i] = (byte)res;
                array[i + 1] = (byte)(res >> 8);

            }
            return array;
        }

        public static async Task<Song> ResolveSong(string query, MusicType musicType = MusicType.Normal) {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentNullException(nameof(query));

            if (musicType != MusicType.Local && IsRadioLink(query)) {
                musicType = MusicType.Radio;
                query = await HandleStreamContainers(query) ?? query;
            }

            try {
                switch (musicType) {
                    case MusicType.Local:
                        return new Song(new SongInfo {
                            Uri = "\"" + Path.GetFullPath(query) + "\"",
                            Title = Path.GetFileNameWithoutExtension(query),
                            Provider = "Local File",
                            ProviderType = musicType,
                        });
                    case MusicType.Radio:
                        return new Song(new SongInfo {
                            Uri = query,
                            Title = $"{query}",
                            Provider = "Radio Stream",
                            ProviderType = musicType,
                        });
                }
                if (SoundCloud.Default.IsSoundCloudLink(query)) {
                    var svideo = await SoundCloud.Default.GetVideoAsync(query);
                    return new Song(new SongInfo {
                        Title = svideo.FullName,
                        Provider = "SoundCloud",
                        Uri = svideo.StreamLink,
                        ProviderType = musicType,
                    });
                }
                var links = await SearchHelper.FindYoutubeUrlByKeywords(query);
                if (links == String.Empty)
                    throw new OperationCanceledException("Not a valid youtube query.");
                var allVideos = await Task.Factory.StartNew(async () => await YouTube.Default.GetAllVideosAsync(links)).Unwrap();
                var videos = allVideos.Where(v => v.AdaptiveKind == AdaptiveKind.Audio);
                var video = videos
                    .Where(v => v.AudioBitrate < 192)
                    .OrderByDescending(v => v.AudioBitrate)
                    .FirstOrDefault();

                if (video == null) // do something with this error
                    throw new Exception("Could not load any video elements based on the query.");
                return new Song(new SongInfo {
                    Title = video.Title.Substring(0, video.Title.Length - 10), // removing trailing "- You Tube"
                    Provider = "YouTube",
                    Uri = video.Uri,
                    ProviderType = musicType,
                });
            } catch (Exception ex) {
                Console.WriteLine($"Failed resolving the link.{ex.Message}");
                return null;
            }
        }

        private static async Task<string> HandleStreamContainers(string query) {
            string file = null;
            try {
                file = await SearchHelper.GetResponseStringAsync(query);
            } catch {
                return query;
            }
            if (query.Contains(".pls")) {
                //File1=http://armitunes.com:8000/
                //Regex.Match(query)
                try {
                    var m = Regex.Match(file, "File1=(?<url>.*?)\\n");
                    var res = m.Groups["url"]?.ToString();
                    return res?.Trim();
                } catch {
                    Console.WriteLine($"Failed reading .pls:\n{file}");
                    return null;
                }
            }
            if (query.Contains(".m3u")) {
                /* 
# This is a comment
                   C:\xxx4xx\xxxxxx3x\xx2xxxx\xx.mp3
                   C:\xxx5xx\x6xxxxxx\x7xxxxx\xx.mp3
                */
                try {
                    var m = Regex.Match(file, "(?<url>^[^#].*)", RegexOptions.Multiline);
                    var res = m.Groups["url"]?.ToString();
                    return res?.Trim();
                } catch {
                    Console.WriteLine($"Failed reading .m3u:\n{file}");
                    return null;
                }

            }
            if (query.Contains(".asx")) {
                //<ref href="http://armitunes.com:8000"/>
                try {
                    var m = Regex.Match(file, "<ref href=\"(?<url>.*?)\"");
                    var res = m.Groups["url"]?.ToString();
                    return res?.Trim();
                } catch {
                    Console.WriteLine($"Failed reading .asx:\n{file}");
                    return null;
                }
            }
            if (query.Contains(".xspf")) {
                /*
                <?xml version="1.0" encoding="UTF-8"?>
                    <playlist version="1" xmlns="http://xspf.org/ns/0/">
                        <trackList>
                            <track><location>file:///mp3s/song_1.mp3</location></track>
                */
                try {
                    var m = Regex.Match(file, "<location>(?<url>.*?)</location>");
                    var res = m.Groups["url"]?.ToString();
                    return res?.Trim();
                } catch {
                    Console.WriteLine($"Failed reading .xspf:\n{file}");
                    return null;
                }
            }

            return query;
        }

        private static bool IsRadioLink(string query) =>
            (query.StartsWith("http") ||
            query.StartsWith("ww"))
            &&
            (query.Contains(".pls") ||
            query.Contains(".m3u") ||
            query.Contains(".asx") ||
            query.Contains(".xspf"));
    }
}
