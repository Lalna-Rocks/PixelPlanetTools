﻿using PixelPlanetUtils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PixelPlanetWatcher
{
    using Pixel = ValueTuple<short, short, PixelColor>;

    class Program
    {

        private static readonly CancellationTokenSource finishCTS = new CancellationTokenSource();
        private static ChunkCache cache;
        private static short x1, y1, x2, y2;
        private static string logFilePath;
        private static string filename;
        private static Logger logger;
        private static List<Pixel> updates = new List<Pixel>();
        private static FileStream lockingStream;
        private static readonly object listLockObj = new object();
        private static readonly Thread saveThread = new Thread(SaveChangesThreadBody);

        static void Main(string[] args)
        {
            try
            {
                try
                {
                    try
                    {
                        x1 = short.Parse(args[0]);
                        y1 = short.Parse(args[1]);
                        x2 = short.Parse(args[2]);
                        y2 = short.Parse(args[3]);
                        if (x1 > x2 || y1 > y2)
                        {
                            throw new Exception();
                        }
                        try
                        {
                            File.Open(args[5], FileMode.Append, FileAccess.Write).Dispose();
                            logFilePath = args[5];
                        }
                        catch
                        { }
                    }
                    catch (OverflowException)
                    {
                        throw new Exception("Entire watched zone should be inside the map");
                    }
                    catch
                    {
                        throw new Exception("Parameters: <leftX> <topY> <rightX> <bottomY> [logFilePath] ; all in range -32768..32767");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    finishCTS.Cancel();
                    return;
                }
                logger = new Logger(finishCTS.Token, logFilePath);
                string fingerprint = Guid.NewGuid().ToString("N");
                cache = new ChunkCache(x1, y1, x2, y2, logger.LogLine);
                bool initialMapStateSaved = false;
                saveThread.Start();
                filename = string.Format("pixels_({0};{1})-({2};{3})_{4:yyyy.MM.dd_HH-mm}.bin", x1, y1, x2, y2, DateTime.Now);
                do
                {
                    try
                    {
                        using (InteractionWrapper wrapper = new InteractionWrapper(fingerprint, logger.LogLine, true))
                        {
                            cache.Wrapper = wrapper;
                            if (!initialMapStateSaved)
                            {
                                Task.Run(() =>
                                {
                                    initialMapStateSaved = true;
                                    cache.DownloadChunks();
                                    using (FileStream fileStream = File.Open(filename, FileMode.Create, FileAccess.Write))
                                    {
                                        using (BinaryWriter writer = new BinaryWriter(fileStream))
                                        {
                                            writer.Write(x1);
                                            writer.Write(y1);
                                            writer.Write(x2);
                                            writer.Write(y2);
                                            writer.Write(DateTime.Now.ToBinary());
                                            for (short y = y1; y <= y2; y++)
                                            {
                                                for (short x = x1; x <= x2; x++)
                                                {
                                                    writer.Write((byte)cache.GetPixelColor(x, y));
                                                }
                                            }
                                        }
                                    }
                                    logger.LogLine("Chunk data is saved to file", MessageGroup.TechInfo);
                                    lockingStream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.None);
                                });
                            }

                            wrapper.OnPixelChanged += (o, e) =>
                            {
                                short x = PixelMap.ConvertToAbsolute(e.Chunk.Item1, e.Pixel.Item1);
                                short y = PixelMap.ConvertToAbsolute(e.Chunk.Item2, e.Pixel.Item2);
                                if (x <= x2 && x >= x1 && y <= y2 && y >= y1)
                                {
                                    logger.LogPixel("Received pixel update:", MessageGroup.PixelInfo, x, y, e.Color);
                                    lock (listLockObj)
                                    {
                                        updates.Add((x, y, e.Color));
                                    }
                                }
                            };
                            wrapper.StartListening();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogLine($"Unhandled exception: {ex.Message}", MessageGroup.Error);
                    }
                } while (true);
            }
            finally
            {
                finishCTS.Cancel();
                Thread.Sleep(1000);
                finishCTS.Dispose();
                logger?.Dispose();
                if (saveThread.IsAlive)
                {
                    saveThread.Interrupt();
                }
            }
        }

        static void SaveChangesThreadBody()
        {
            Task delayTask = Task.Delay(TimeSpan.FromMinutes(1), finishCTS.Token);
            try
            {
                do
                {
                    if (finishCTS.IsCancellationRequested)
                    {
                        return;
                    }
                    try
                    {
                        delayTask.Wait();
                        delayTask = Task.Delay(TimeSpan.FromMinutes(1), finishCTS.Token);
                    }
                    catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
                    {
                        return;
                    }
                    List<Pixel> saved;
                    lock (listLockObj)
                    {
                        saved = updates;
                        updates = new List<Pixel>();
                    }
                    lockingStream.Close();
                    using (FileStream fileStream = File.Open(filename, FileMode.Append, FileAccess.Write))
                    {
                        using (BinaryWriter writer = new BinaryWriter(fileStream))
                        {
                            writer.Write(DateTime.Now.ToBinary());
                            writer.Write((uint)saved.Count);
                            foreach ((short, short, PixelColor) pixel in saved)
                            {
                                writer.Write(pixel.Item1);
                                writer.Write(pixel.Item2);
                                writer.Write((byte)pixel.Item3);
                            }
                        }
                    }
                    logger.LogLine($"{saved.Count} pixel updates are saved to file", MessageGroup.TechInfo);
                    lockingStream  = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.None);
                } while (true);
            }
            catch (ThreadInterruptedException)
            {
                using (FileStream fileStream = File.Open(filename, FileMode.Append, FileAccess.Write))
                {
                    lockingStream.Close();
                    using (BinaryWriter writer = new BinaryWriter(fileStream))
                    {
                        writer.Write(DateTime.Now.ToBinary());
                        writer.Write((uint)updates.Count);
                        foreach ((short, short, PixelColor) pixel in updates)
                        {
                            writer.Write(pixel.Item1);
                            writer.Write(pixel.Item2);
                            writer.Write((byte)pixel.Item3);
                        }
                        logger.LogLine($"{updates.Count} pixel updates are saved to file", MessageGroup.TechInfo);
                    }
                }
            }
        }
    }
}