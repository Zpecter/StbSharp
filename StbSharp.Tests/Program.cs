﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Sichem;
using StbNative;

namespace StbSharp.Tests
{
	internal static class Program
	{
		private delegate void WriteDelegate(Image image, Stream stream);

		private const int LoadTries = 10;
		private const int ThreadsCount = 10;

		private static readonly int[] JpgQualities = {1, 4, 8, 16, 25, 32, 50, 64, 72, 80, 90, 100};
		private static readonly string[] FormatNames = {"BMP", "TGA", "HDR", "PNG", "JPG"};

		private class ThreadInfo
		{
			public string[] Files { get; set; }
			public bool Success { get; set; }
			public bool Finished { get; set; }
		}


		public static void Log(string message)
		{
			Console.WriteLine(Thread.CurrentThread.ManagedThreadId + " -- " + message);
		}

		public static void Log(string format, params object[] args)
		{
			Log(string.Format(format, args));
		}

		private static void BeginWatch(Stopwatch sw)
		{
			sw.Restart();
		}

		private static int EndWatch(Stopwatch sw)
		{
			sw.Stop();
			return (int) sw.ElapsedMilliseconds;
		}

		private delegate byte[] LoadDelegate(out int x, out int y, out int comp);

		private static void ParseTest(Stopwatch sw, LoadDelegate load1, LoadDelegate load2,
			out int load1Passed, out int load2Passed)
		{
			Log("With StbSharp");
			int x = 0, y = 0, comp = 0;
			byte[] parsed = new byte[0];
			BeginWatch(sw);

			for (var i = 0; i < LoadTries; ++i)
			{
				parsed = load1(out x, out y, out comp);
			}

			Log("x: {0}, y: {1}, comp: {2}, size: {3}", x, y, comp, parsed.Length);
			var passed = EndWatch(sw)/LoadTries;
			Log("Span: {0} ms", passed);
			load1Passed = passed;

			Log("With Stb.Native");
			int x2 = 0, y2 = 0, comp2 = 0;
			byte[] parsed2 = new byte[0];

			BeginWatch(sw);
			for (var i = 0; i < LoadTries; ++i)
			{
				parsed2 = load2(out x2, out y2, out comp2);
			}
			Log("x: {0}, y: {1}, comp: {2}, size: {3}", x2, y2, comp2, parsed2.Length);
			passed = EndWatch(sw)/LoadTries;
			Log("Span: {0} ms", passed);
			load2Passed = passed;

			if (x != x2)
			{
				throw new Exception(string.Format("Inconsistent x: StbSharp={0}, Stb.Native={1}", x, x2));
			}

			if (y != y2)
			{
				throw new Exception(string.Format("Inconsistent y: StbSharp={0}, Stb.Native={1}", y, y2));
			}

			if (comp != comp2)
			{
				throw new Exception(string.Format("Inconsistent comp: StbSharp={0}, Stb.Native={1}", comp, comp2));
			}

			if (parsed.Length != parsed2.Length)
			{
				throw new Exception(string.Format("Inconsistent parsed length: StbSharp={0}, Stb.Native={1}", parsed.Length,
					parsed2.Length));
			}

			for (var i = 0; i < parsed.Length; ++i)
			{
				if (parsed[i] != parsed2[i])
				{
					throw new Exception(string.Format("Inconsistent data: index={0}, StbSharp={1}, Stb.Native={2}",
						i,
						(int) parsed[i],
						(int) parsed2[i]));
				}
			}
		}

		public static bool RunTests()
		{
			var imagesPath = "..\\..\\..\\TestImages";

			var files = Directory.EnumerateFiles(imagesPath, "*.*", SearchOption.AllDirectories).ToArray();
			Log("Files count: {0}", files.Length);

			var filesByThreads = new ThreadInfo[ThreadsCount];
			var filesPerThread = files.Length/ThreadsCount;

			var threadNum = 0;
			var threadFiles = new List<string>();
			foreach (var file in files)
			{
				if (threadNum < ThreadsCount - 1 && threadFiles.Count >= filesPerThread)
				{
					filesByThreads[threadNum] = new ThreadInfo
					{
						Files = threadFiles.ToArray()
					};

					threadFiles.Clear();
					++threadNum;
				}

				threadFiles.Add(file);
			}

			filesByThreads[threadNum] = new ThreadInfo
			{
				Files = threadFiles.ToArray()
			};

			for (var i = 0; i < ThreadsCount; ++i)
			{
				ThreadPool.QueueUserWorkItem(ThreadProc, filesByThreads[i]);
			}

			while (true)
			{
				Thread.Sleep(1000);
				var finished = true;
				foreach (var ti in filesByThreads)
				{
					if (ti.Finished && !ti.Success)
					{
						return false;
					}
				}

				foreach (var ti in filesByThreads)
				{
					if (!ti.Finished)
					{
						finished = false;
						break;
					}
				}

				if (finished)
				{
					break;
				}
			}

			var result = true;
			foreach (var ti in filesByThreads)
			{
				if (!ti.Success)
				{
					result = false;
					break;
				}
			}

			return result;
		}

		private static void ThreadProc(object state)
		{
			var threadInfo = (ThreadInfo)state;

			try
			{
				var sw = new Stopwatch();

				var stbSharpLoadingFromStream = 0;
				var stbNativeLoadingFromStream = 0;
				var stbSharpLoadingFromMemory = 0;
				var stbNativeLoadingFromMemory = 0;
				var stbSharpWrite = 0;
				var stbNativeWrite = 0;
				var stbSharpCompression = 0;
				var stbNativeCompression = 0;

				var files = threadInfo.Files;

				int filesProcessed = 0;

				foreach (var f in files)
				{
					if (!f.EndsWith(".bmp") && !f.EndsWith(".jpg") && !f.EndsWith(".png") &&
					    !f.EndsWith(".jpg") && !f.EndsWith(".psd") && !f.EndsWith(".pic") &&
					    !f.EndsWith(".tga"))
					{
						continue;
					}

					Log(string.Empty);
					Log("{0} -- #{1}: Loading {2} into memory", DateTime.Now.ToLongTimeString(), filesProcessed, f);
					var data = File.ReadAllBytes(f);
					Log("----------------------------");

					Log("Loading From Stream");
					int x = 0, y = 0, comp = 0;
					int stbSharpPassed, stbNativePassed;
					byte[] parsed = new byte[0];
					ParseTest(
						sw,
						(out int xx, out int yy, out int ccomp) =>
						{
							using (var ms = new MemoryStream(data))
							{
								var loader = new ImageReader();
								var img = loader.Read(ms);

								parsed = img.Data;
								xx = img.Width;
								yy = img.Height;
								ccomp = img.SourceComp;

								x = xx;
								y = yy;
								comp = ccomp;
								return parsed;
							}
						},
						(out int xx, out int yy, out int ccomp) =>
						{
							using (var ms = new MemoryStream(data))
							{
								return Native.load_from_stream(ms, out xx, out yy, out ccomp, Stb.STBI_default);
							}
						},
						out stbSharpPassed, out stbNativePassed
						);
					stbSharpLoadingFromStream += stbSharpPassed;
					stbNativeLoadingFromStream += stbNativePassed;

					Log("Loading from memory");
					ParseTest(
						sw,
						(out int xx, out int yy, out int ccomp) =>
						{
							var img = Stb.LoadFromMemory(data);

							var res = img.Data;
							xx = img.Width;
							yy = img.Height;
							ccomp = img.SourceComp;

							x = xx;
							y = yy;
							comp = ccomp;
							return res;
						},
						(out int xx, out int yy, out int ccomp) =>
							Native.load_from_memory(data, out xx, out yy, out ccomp, Stb.STBI_default),
						out stbSharpPassed, out stbNativePassed
						);
					stbSharpLoadingFromMemory += stbSharpPassed;
					stbNativeLoadingFromMemory += stbNativePassed;

					var image = new Image
					{
						Comp = comp,
						Data = parsed,
						Width = x,
						Height = y
					};

					for (var k = 0; k <= 4; ++k)
					{
						Log("Saving as {0} with StbSharp", FormatNames[k]);

						if (k < 4)
						{
							var writer = new ImageWriter();
							WriteDelegate wd = null;
							switch (k)
							{
								case 0:
									wd = writer.WriteBmp;
									break;
								case 1:
									wd = writer.WriteTga;
									break;
								case 2:
									wd = writer.WriteHdr;
									break;
								case 3:
									wd = writer.WritePng;
									break;
							}

							byte[] save;
							BeginWatch(sw);
							using (var stream = new MemoryStream())
							{
								wd(image, stream);
								save = stream.ToArray();
							}
							var passed = EndWatch(sw);
							stbSharpWrite += passed;
							Log("Span: {0} ms", passed);
							Log("StbSharp Size: {0}", save.Length);

							Log("Saving as {0} with Stb.Native", FormatNames[k]);
							BeginWatch(sw);
							byte[] save2;
							using (var stream = new MemoryStream())
							{
								Native.save_to_stream(parsed, x, y, comp, k, stream);
								save2 = stream.ToArray();
							}

							passed = EndWatch(sw);
							stbNativeWrite += passed;

							Log("Span: {0} ms", passed);
							Log("Stb.Native Size: {0}", save2.Length);

							if (save.Length != save2.Length)
							{
								throw new Exception(string.Format("Inconsistent output size: StbSharp={0}, Stb.Native={1}",
									save.Length, save2.Length));
							}

							for (var i = 0; i < save.Length; ++i)
							{
								if (save[i] != save2[i])
								{
									throw new Exception(string.Format("Inconsistent data: index={0}, StbSharp={1}, Stb.Native={2}",
										i,
										(int) save[i],
										(int) save2[i]));
								}
							}
						}
						else
						{
							for (var qi = 0; qi < JpgQualities.Length; ++qi)
							{
								var quality = JpgQualities[qi];
								Log("Saving as JPG with StbSharp with quality={0}", quality);
								byte[] save;
								BeginWatch(sw);
								using (var stream = new MemoryStream())
								{
									var writer = new ImageWriter();
									writer.WriteJpg(image, stream, quality);
									save = stream.ToArray();
								}
								var passed = EndWatch(sw);
								stbSharpWrite += passed;

								Log("Span: {0} ms", passed);
								Log("StbSharp Size: {0}", save.Length);

								Log("Saving as JPG with Stb.Native with quality={0}", quality);
								BeginWatch(sw);
								byte[] save2;
								using (var stream = new MemoryStream())
								{
									Native.save_to_jpg(parsed, x, y, comp, stream, quality);
									save2 = stream.ToArray();
								}

								passed = EndWatch(sw);
								stbNativeWrite += passed;

								Log("Span: {0} ms", passed);
								Log("Stb.Native Size: {0}", save2.Length);

								if (save.Length != save2.Length)
								{
									throw new Exception(string.Format("Inconsistent output size: StbSharp={0}, Stb.Native={1}",
										save.Length, save2.Length));
								}

								for (var i = 0; i < save.Length; ++i)
								{
									if (save[i] != save2[i])
									{
										throw new Exception(string.Format("Inconsistent data: index={0}, StbSharp={1}, Stb.Native={2}",
											i,
											(int) save[i],
											(int) save2[i]));
									}
								}
							}
						}
					}

					// Compressing
					Log("Performing DXT compression with StbSharp");
					image = Stb.LoadFromMemory(data, Stb.STBI_rgb_alpha);

					BeginWatch(sw);
					var compressed = Stb.stb_compress_dxt(image);
					stbSharpCompression += EndWatch(sw);

					Log("Performing DXT compression with Stb.Native");
					BeginWatch(sw);
					var compressed2 = Native.compress_dxt(image.Data, image.Width, image.Height, true);
					stbNativeCompression += EndWatch(sw);

					if (compressed.Length != compressed2.Length)
					{
						throw new Exception(string.Format("Inconsistent output size: StbSharp={0}, Stb.Native={1}",
							compressed.Length, compressed2.Length));
					}

					for (var i = 0; i < compressed.Length; ++i)
					{
						if (compressed[i] != compressed2[i])
						{
							throw new Exception(string.Format("Inconsistent data: index={0}, StbSharp={1}, Stb.Native={2}",
								i,
								(int) compressed[i],
								(int) compressed2[i]));
						}
					}


					++filesProcessed;

					Log("Total StbSharp Loading From Stream Time: {0} ms", stbSharpLoadingFromStream);
					Log("Total Stb.Native Loading From Stream Time: {0} ms", stbNativeLoadingFromStream);
					Log("Total StbSharp Loading From memory Time: {0} ms", stbSharpLoadingFromMemory);
					Log("Total Stb.Native Loading From memory Time: {0} ms", stbNativeLoadingFromMemory);
					Log("Total StbSharp Write Time: {0} ms", stbSharpWrite);
					Log("Total Stb.Native Write Time: {0} ms", stbNativeWrite);
					Log("Total StbSharp Compression Time: {0} ms", stbSharpCompression);
					Log("Total Stb.Native Compression Time: {0} ms", stbNativeCompression);

					Log("GC Memory: {0}", GC.GetTotalMemory(true));
					Log("Sichem Allocated: {0}", Operations.AllocatedTotal);
				}

				Log(DateTime.Now.ToLongTimeString() + " -- " + " Files processed: " + filesProcessed);

				threadInfo.Success = true;
			}
			catch (Exception ex)
			{
				Log("Error: " + ex.Message);
			}

			threadInfo.Finished = true;
		}

		public static int Main(string[] args)
		{
			var start = DateTime.Now;
			var res = RunTests();
			var passed = DateTime.Now - start;
			Log("Span: {0} ms", passed.TotalMilliseconds);
			Log(DateTime.Now.ToLongTimeString() + " -- " + (res ? "Success" : "Failure"));

			return res ? 1 : 0;
		}
	}
}