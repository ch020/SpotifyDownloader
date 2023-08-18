using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using SpotifyExplode;
using SpotifyExplode.Playlists;
using SpotifyExplode.Tracks;
using YoutubeExplode;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using Squirrel;

namespace SpotifyDownloader
{
    internal class Program
	{
        const string spotify_pattern = @"(https?:\/\/open.spotify.com\/(track|playlist|artist|album)\/[a-zA-Z0-9]+(\/playlist\/[a-zA-Z0-9]+|)|spotify:(track|playlist|artist|album):[a-zA-Z0-9]+(:playlist:[a-zA-Z0-9]+|))";
        static async Task Main(string[] args)
		{
            ProgramStart:
            AnsiConsole.Clear();
            string type = "track";
            string url = AnsiConsole.Prompt(new TextPrompt<string>("Please enter a [lime]Spotify[/] URL:")
                .PromptStyle("lime")
                .Validate(input =>
                {
                    if (string.IsNullOrWhiteSpace(input)) return ValidationResult.Error("[red]URL cannot be empty[/]");
                    var match = Regex.Match(input, spotify_pattern);
                    if (!match.Success) return ValidationResult.Error("[red]Input does not contain a valid Spotify URL[/]");
                    else
                    {
                        type = match.Groups[2].Value;
                        return ValidationResult.Success();
                    }
                }));
            var spotify = new SpotifyClient();
            var youtube = new YoutubeClient();
            var client = new HttpClient();

        RetrieveDetails:
            List<Track> tracks = new List<Track>();
            if (type == "track")
            {
                try
                {
                    TrackId trackId = TrackId.Parse(url);
                    await AnsiConsole.Status()
                            .Spinner(Spinner.Known.Dots)
                            .SpinnerStyle(Style.Parse("cyan"))
                            .StartAsync("Retrieving Details", async _ =>
                            {
                                var track = await spotify.Tracks.GetAsync(trackId);
                                tracks.Add(track);
                            });
                }
                catch (Exception)
                {
                    AnsiConsole.MarkupLine("[red]ERROR[/]: Cannot connect to Spotify!");
                    if (AnsiConsole.Confirm("Try again?")) goto RetrieveDetails;
                    else return;
                }
            }
            else if (type == "playlist")
            {
                try
                {
                    PlaylistId playlistId = PlaylistId.Parse(url);
                    await AnsiConsole.Status()
                            .Spinner(Spinner.Known.Dots)
                            .SpinnerStyle(Style.Parse("cyan"))
                            .StartAsync("Retrieving Details", async _ =>
                            {
                                var tracksTemp = await spotify.Playlists.GetAllTracksAsync(playlistId);
                                tracks = tracksTemp;
                            });
                }
                catch (Exception)
                {
                    AnsiConsole.MarkupLine("[red]ERROR[/]: Cannot connect to Spotify!");
                    if (AnsiConsole.Confirm("Try again?")) goto RetrieveDetails;
                    else return;
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[red]ERROR[/]: URL must be either track or playlist!");
            }
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine((tracks.Count > 1 ? "Tracks" : "Track") + " [lime]FOUND[/]!");
            var table = new Table();
            table.AddColumns("[cyan]Title[/]", "[cyan]Artist(s)[/]", "[cyan]Album[/]", "[cyan]Duration[/]");
            foreach (var track in tracks) table.AddRow(track.Title.SanitizeMarkup(), string.Join(", ", track.Artists.Select(x => x.Name.SanitizeMarkup())), track.Album?.Name.SanitizeMarkup(), track.DurationMs.ToHumanReadable());
            AnsiConsole.Write(table);
            Dictionary<Track, VideoId> downloadIds = new Dictionary<Track, VideoId>();
            if (!AnsiConsole.Prompt(new ConfirmationPrompt(tracks.Count > 1 ? "Download All?" : "Download?")))
            {
                AnsiConsole.Clear();
                tracks = AnsiConsole.Prompt(new MultiSelectionPrompt<Track>
                {
                    Converter = x => x.Title,
                    Title = "Please select the tracks to download",
                    Required = true,
                    HighlightStyle = new Style(foreground: Color.Lime, decoration: Decoration.Bold),
                    InstructionsText = "[grey](Press [blue]<space>[/] to toggle a song, [green]<enter>[/] to accept selections)[/]",
                    MoreChoicesText = "[grey](Move up and down to reveal more songs)[/]",
                }.AddChoices(tracks));
            }

            AnsiConsole.Clear();
            string downloadDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            var displayPanel = new Panel(new TextPath(downloadDirectory)) { Header = new PanelHeader("[cyan]Output Directory[/]") };
            AnsiConsole.Write(displayPanel);

            if (AnsiConsole.Prompt(new ConfirmationPrompt("Change Directory?") { DefaultValue = false })) {
                do
                {
                    downloadDirectory = AnsiConsole.Prompt(new TextPrompt<string>("Please enter the new [yellow]directory[/]: ")
                    {
                        AllowEmpty = false,
                        Validator = x =>
                        {
                            try
                            {
                                Path.GetFullPath(x);
                                if (!Directory.Exists(x)) return ValidationResult.Error("[red]Directory doesn't exist[/]");
                                return ValidationResult.Success();
                            }
                            catch (Exception) { return ValidationResult.Error("[red]Path is invalid as directory[/]"); }
                        }
                    });
                    AnsiConsole.Clear();
                    displayPanel = new Panel(new TextPath(downloadDirectory)) { Header = new PanelHeader("[cyan]Output Directory[/]") };
                    AnsiConsole.Write(displayPanel);
                } while (AnsiConsole.Prompt(new ConfirmationPrompt("Change Directory?") { DefaultValue = false }));
            }

            RetrieveDownloadInfo:
            try
            {
                await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .SpinnerStyle(Style.Parse("cyan"))
                        .StartAsync("Retrieving Download Information", async _ => {
                            await Task.WhenAll(tracks.Select(async track =>
                            {
                                var id = await spotify.Tracks.GetYoutubeIdAsync(track.Id);
                                downloadIds.Add(track, VideoId.Parse(id));
                            }));
                        });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[red]ERROR[/]: Cannot connect to Spotify!");
                AnsiConsole.WriteException(ex);
                if (AnsiConsole.Confirm("Try again?")) goto RetrieveDownloadInfo;
                else return;
            }

            RetrieveStreams:
            Dictionary<Track, IStreamInfo> streamInfos = new Dictionary<Track, IStreamInfo>();
            try
            {
                await AnsiConsole.Status()
                       .Spinner(Spinner.Known.Dots)
                       .SpinnerStyle(Style.Parse("cyan"))
                       .StartAsync("Retrieving Streams", async _ => {
                           await Task.WhenAll(downloadIds.Select(async item =>
                           {
                               try
                               {
                                   var streamManifest = await youtube.Videos.Streams.GetManifestAsync(item.Value);
                                   var streamInfo = streamManifest.GetAudioOnlyStreams().TryGetWithHighestBitrate();
                                   if (streamInfo == null)
                                   {
                                       AnsiConsole.MarkupLine($"[yellow]WARNING[/]: Cannot retrieve stream for song '[cyan]{item.Key.Title}[/]'");
                                       return;
                                   }
                                   streamInfos.Add(item.Key, streamInfo);
                               }
                               catch (VideoUnplayableException)
                               {
                                    AnsiConsole.MarkupLine($"[yellow]WARNING[/]: Cannot retrieve stream for song '[cyan]{item.Key?.Title}[/]'");
                                    return;
                               }
                           }));
                       });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[red]ERROR[/]: Cannot connect to Spotify!");
                AnsiConsole.WriteException(ex);
                if (AnsiConsole.Confirm("Try again?")) goto RetrieveStreams;
                else return;
            }
            if (streamInfos.Count <= 0)
            {
                AnsiConsole.MarkupLine("[red]ERROR[/]: No streams were found!");
                if (AnsiConsole.Confirm("Try again?")) goto ProgramStart;
                else return;
            }

            Dictionary<string, Track> downloads = new Dictionary<string, Track>();

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await AnsiConsole.Progress().AutoClear(true).Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn()
                }).StartAsync(async ctx =>
                    {
                        await Task.WhenAll(streamInfos.Select(async item =>
                        {
                            var task = ctx.AddTask(item.Key.Title.SanitizeMarkup(), new ProgressTaskSettings
                     {
                         AutoStart = false,
                         MaxValue = 1
                     });
                            string downloadPath = Path.Combine(downloadDirectory, item.Key.Title.RemoveInvalidChars() + ".mp3");
                            await youtube.Videos.Streams.DownloadAsync(item.Value, downloadPath, progress: task,cancellationToken:cts.Token);
                            downloads.Add(downloadPath, item.Key);
                        }));
                    });
                AnsiConsole.MarkupLine("Download(s) [lime]COMPLETED[/]");
                if (AnsiConsole.Prompt(new ConfirmationPrompt("Would you like to view the files?"))) { Utils.OpenDirectory(downloadDirectory); }
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("Download(s) [yellow]PARTIALLY COMPLETED[/]");
                var t = new Table();
                t.AddColumns("[cyan]Track[/]","[cyan]Status[/]");
                foreach (var track in tracks)
                {
                    if (!streamInfos.ContainsKey(track)) t.AddRow(track.Title, "[red]Stream Not Found[/]");
                    else if (!downloads.ContainsValue(track)) t.AddRow(track.Title, "[red]Download Failed[/]");
                    else t.AddRow(track.Title, "[green]Download Successful[/]");
                }
                AnsiConsole.Write(t);
                if (AnsiConsole.Prompt(new ConfirmationPrompt("Would you like to view the files?"))) { Utils.OpenDirectory(downloadDirectory); }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("Download(s) [red]FAILED[/]");
                AnsiConsole.WriteException(ex);
                Console.ReadKey();
            }
            finally { cts.Dispose(); }
        }
	}
}
