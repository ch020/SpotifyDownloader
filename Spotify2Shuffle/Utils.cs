using Spectre.Console;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SpotifyDownloader
{
    public static class Utils
    {
        public static string SanitizeMarkup(this string input) => input.Replace("[", "[[").Replace("]", "]]");
        public static string ToHumanReadable(this long ms)
        {
            TimeSpan t = TimeSpan.FromMilliseconds(ms);
            return string.Format("{0:D2}m:{1:D2}s",
                        t.Minutes,
                        t.Seconds);
        }
        public static async Task Download(HttpClient client, ProgressTask task, string url)
        {
            try
            {
                using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    // Set the max value of the progress task to the number of bytes
                    task.MaxValue(response.Content.Headers.ContentLength ?? 0);
                    // Start the progress task
                    task.StartTask();

                    var filename = url.Substring(url.LastIndexOf('/') + 1);
                    AnsiConsole.MarkupLine($"Starting download of [u]{filename}[/] ({task.MaxValue} bytes)");

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        while (true)
                        {
                            var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                            if (read == 0)
                            {
                                AnsiConsole.MarkupLine($"Download of [u]{filename}[/] [green]completed![/]");
                                break;
                            }

                            // Increment the number of read bytes for the progress task
                            task.Increment(read);

                            // Write the read bytes to the output stream
                            await fileStream.WriteAsync(buffer, 0, read);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // An error occured
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex}");
            }
        }
        private static readonly char[] IllegalChars = Path.GetInvalidFileNameChars();
        public static string RemoveInvalidChars(this string filename) => new string(filename.Where(x => !IllegalChars.Contains(x)).ToArray());
        public static string[] GetAllFiles(string directory)
        {
            List<string> result = new List<string>();
            foreach (var d in Directory.EnumerateDirectories(directory))
            {
                foreach (var file in Directory.EnumerateFiles(d)) result.Add(file);
            }
            return result.ToArray();
        }
        public static void OpenDirectory(string directory)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
            {
                FileName = directory + (directory.EndsWith("/") || directory.EndsWith("\\") ? Path.DirectorySeparatorChar.ToString() : ""),
                UseShellExecute = true,
                Verb = "open"
            });
        }
    }
}
