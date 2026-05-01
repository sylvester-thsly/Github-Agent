using Microsoft.Extensions.Configuration;
using Octokit;
using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using System.Linq;
using Newtonsoft.Json;
using System.Text;
using System.Collections.Generic;

class Program
{
    static async Task Main(string[] args)
    {
        AnsiConsole.MarkupLine("[bold blue]📡 Project Pulse: Initializing...[/]");
        
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        string githubToken = config["Github:Token"] ?? "";
        string openRouterKey = config["OpenRouter:ApiKey"] ?? "";
        string slackBotToken = config["Slack:BotToken"] ?? "";
        string slackAppToken = config["Slack:AppToken"] ?? "";
        string slackChannel = config["Slack:Channel"] ?? "general";
        
        var client = new GitHubClient(new ProductHeaderValue("ProjectPulse"));
        client.Credentials = new Credentials(githubToken);

        string currentUserName = "";
        try {
            var user = await client.User.Current();
            currentUserName = user.Login;
            AnsiConsole.MarkupLine($"👋 [green]Connected to GitHub as:[/] [bold]{currentUserName}[/]");
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"❌ [red]Connection failed:[/] {ex.Message}");
            return;
        }  
        
        var personalRepos = new List<Repository>();
        var orgRepos = new List<Repository>();
        var collaborationRepos = new List<Repository>();

        await AnsiConsole.Status().StartAsync("Analyzing GitHub Access...", async ctx => {
            // Fetch EVERYTHING
            var allRepos = (await client.Repository.GetAllForCurrent(new RepositoryRequest { Type = RepositoryType.All })).ToList();
            
            AnsiConsole.MarkupLine($"🔍 [grey]Debug: Found {allRepos.Count} total repositories across all owners.[/]");

            foreach (var repo in allRepos) {
                if (repo.Owner.Login == currentUserName) {
                    personalRepos.Add(repo);
                } else if (repo.Owner.Type == AccountType.Organization) {
                    orgRepos.Add(repo);
                } else {
                    collaborationRepos.Add(repo);
                }
            }

            // Explicit Org check
            try {
                var orgs = await client.Organization.GetAllForCurrent();
                AnsiConsole.MarkupLine($"🔍 [grey]Debug: Token sees {orgs.Count} formal Organization memberships.[/]");
                foreach (var org in orgs) {
                    var repos = await client.Repository.GetAllForOrg(org.Login);
                    foreach (var r in repos) {
                        if (!orgRepos.Any(existing => existing.Id == r.Id)) orgRepos.Add(r);
                    }
                }
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"🔍 [grey]Debug: Org-specific fetch failed: {ex.Message}[/]");
            }
        });

        var categories = new Dictionary<string, List<Repository>>();
        if (personalRepos.Count > 0) categories.Add("[bold green]👤 My Personal Projects[/]", personalRepos.OrderByDescending(r => r.UpdatedAt).ToList());
        if (orgRepos.Count > 0) categories.Add("[bold blue]🏢 Organization Projects[/]", orgRepos.OrderByDescending(r => r.UpdatedAt).ToList());
        if (collaborationRepos.Count > 0) categories.Add("[bold yellow]🤝 Shared & Collaborations[/]", collaborationRepos.OrderByDescending(r => r.UpdatedAt).ToList());

        if (categories.Count == 0) {
            AnsiConsole.MarkupLine("❌ [red]No repositories found.[/]");
            return;
        }

        var selectedCategoryLabel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("\n[yellow]Select a Workspace Category:[/]")
                .AddChoices(categories.Keys));

        var selectedCategoryRepos = categories[selectedCategoryLabel];

        var selectedName = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"\n[blue]Select a Project:[/]")
                .PageSize(15)
                .AddChoices(selectedCategoryRepos.Select(r => r.FullName)));

        var selectedRepo = selectedCategoryRepos.First(r => r.FullName == selectedName);

        // --- 📊 VISUAL RECONNAISSANCE ---
        AnsiConsole.MarkupLine($"\n🕵️‍♂️ [yellow]Scanning {selectedRepo.FullName}...[/]");
        var commits = (await client.Repository.Commit.GetAll(selectedRepo.Owner.Login, selectedRepo.Name)).ToList();
        var issues = (await client.Issue.GetAllForRepository(selectedRepo.Owner.Login, selectedRepo.Name, new RepositoryIssueRequest { State = ItemStateFilter.Open })).ToList();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[blue]Recent Commits[/]");
        table.AddColumn("[red]Open Issues[/]");
        for (int i = 0; i < 3; i++) {
            string c = i < commits.Count ? commits[i].Commit.Message : "-";
            string iss = i < issues.Count ? issues[i].Title : "-";
            table.AddRow(c, iss);
        }
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n🌲 [yellow]Mapping Repository Tree...[/]");
        var treeBuilder = new StringBuilder();
        await ScanRepository(client, selectedRepo.Owner.Login, selectedRepo.Name, "", treeBuilder, true);

        // --- 🧠 INITIAL PM ASSIGNMENT ---
        AnsiConsole.MarkupLine("\n🧠 [yellow]Generating PM Assignment...[/]");
        
        string contextPrompt = "You are an ELITE Senior Product Manager. Your job is to lead. " +
                               "Analyze this project state and provide a strategic 'Action Plan'. " +
                               "DO NOT ASK QUESTIONS. Tell the team what the priorities are and assign 3 specific technical 'jobs'.\n\n" +
                               $"Project: {selectedRepo.FullName}\n" +
                               $"Recent Activity: {string.Join(", ", commits.Take(5).Select(c => c.Commit.Message))}\n" +
                               $"Architecture: {treeBuilder.ToString().Substring(0, Math.Min(1000, treeBuilder.Length))}";

        string aiResponse = await GetAiResponseWithFallback(openRouterKey, contextPrompt);
        
        await JoinSlackChannel(slackBotToken, slackChannel);
        
        string welcomeMsg = $"🏗️ *Project Pulse: {selectedRepo.FullName} is LIVE*\n\n" +
                            $"📍 *Strategic Action Plan:*\n{aiResponse}\n\n" +
                            $"🤖 _I am your PM. Message me anytime for task details or code reviews._";
        
        await SendSlackMessage(slackBotToken, slackChannel, welcomeMsg);

        AnsiConsole.MarkupLine($"\n🚀 [bold green]Ghost Mode Active![/]");
        await StartSlackListener(slackAppToken, openRouterKey, slackBotToken, slackChannel, selectedRepo.FullName, treeBuilder.ToString());
    }

    static async Task StartSlackListener(string appToken, string aiKey, string botToken, string channel, string repoFullName, string repoTree)
    {
        while (true) {
            try {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {appToken}");
                var response = await httpClient.PostAsync("https://slack.com/api/apps.connections.open", null);
                var responseString = await response.Content.ReadAsStringAsync();
                dynamic? result = JsonConvert.DeserializeObject(responseString);
                if (result == null || result.ok == false) { await Task.Delay(5000); continue; }
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri((string)result.url), CancellationToken.None);
                AnsiConsole.MarkupLine("✅ [green]Connected to Slack[/]");
                var buffer = new byte[1024 * 16];
                while (ws.State == WebSocketState.Open) {
                    var resultSegment = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (resultSegment.MessageType == WebSocketMessageType.Close) break;
                    string message = Encoding.UTF8.GetString(buffer, 0, resultSegment.Count);
                    dynamic? envelope = JsonConvert.DeserializeObject(message);
                    if (envelope?.envelope_id != null) {
                        var ack = JsonConvert.SerializeObject(new { envelope_id = (string)envelope.envelope_id });
                        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(ack)), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    if (envelope?.type == "events_api") {
                        var evt = envelope.payload.@event;
                        if (evt != null && evt.type == "message" && evt.bot_id == null && evt.text != null) {
                            string userText = (string)evt.text;
                            AnsiConsole.MarkupLine($"💬 [grey]Input:[/] {userText}");
                            string pmPrompt = $"You are the AI Product Manager for {repoFullName}.\n" +
                                              $"Code Context: {repoTree.Substring(0, Math.Min(1000, repoTree.Length))}\n" +
                                              $"User: {userText}";
                            string aiReply = await GetAiResponseWithFallback(aiKey, pmPrompt);
                            await SendSlackMessage(botToken, channel, aiReply);
                        }
                    }
                }
            } catch { await Task.Delay(3000); }
        }
    }

    static async Task<string> GetAiResponseWithFallback(string apiKey, string prompt)
    {
        string[] models = { "google/gemini-flash-1.5", "openai/gpt-4o-mini", "anthropic/claude-3-haiku" };
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        foreach (var model in models) {
            try {
                var requestBody = new { model = model, messages = new[] { new { role = "user", content = prompt } } };
                var response = await httpClient.PostAsync("https://openrouter.ai/api/v1/chat/completions", new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"));
                if (response.IsSuccessStatusCode) {
                    var responseString = await response.Content.ReadAsStringAsync();
                    dynamic? result = JsonConvert.DeserializeObject(responseString);
                    if (result?.choices != null && result.choices.Count > 0) return (string)result.choices[0].message.content;
                }
            } catch { }
        }
        return "⚠️ AI Error: All intelligence endpoints are currently unavailable.";
    }

    static async Task JoinSlackChannel(string botToken, string channelName)
    {
        try {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {botToken}");
            var listRes = await httpClient.GetAsync("https://slack.com/api/conversations.list?types=public_channel&limit=200");
            var listContent = await listRes.Content.ReadAsStringAsync();
            dynamic? listData = JsonConvert.DeserializeObject(listContent);
            if (listData?.channels == null) return;
            foreach (var ch in listData.channels) {
                if ((string)ch.name == channelName) {
                    var joinContent = new StringContent(JsonConvert.SerializeObject(new { channel = (string)ch.id }), Encoding.UTF8, "application/json");
                    await httpClient.PostAsync("https://slack.com/api/conversations.join", joinContent);
                    break;
                }
            }
        } catch { }
    }

    static async Task SendSlackMessage(string botToken, string channel, string message)
    {
        try {
            if (string.IsNullOrEmpty(botToken)) return;
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {botToken}");
            var payload = new { channel = channel, text = message };
            await httpClient.PostAsync("https://slack.com/api/chat.postMessage", new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"));
        } catch { }
    }

    static async Task ScanRepository(GitHubClient client, string owner, string name, string path, StringBuilder treeBuilder, bool visualize = false)
    {
        try {
            var contents = await client.Repository.Content.GetAllContents(owner, name, path ?? "");
            foreach (var item in contents) {
                string indent = new string(' ', (path?.Split('/', StringSplitOptions.RemoveEmptyEntries).Length ?? 0) * 2);
                if (visualize) AnsiConsole.MarkupLine($"{indent}{(item.Type == ContentType.Dir ? "📂 [bold blue]" : "📄 [grey]")}{item.Name}{(item.Type == ContentType.Dir ? "/" : "")}[/]");
                if (item.Type == ContentType.Dir) {
                    treeBuilder.AppendLine($"{indent}FOLDER: {item.Name}");
                    await ScanRepository(client, owner, name, item.Path, treeBuilder, visualize);
                } else {
                    treeBuilder.AppendLine($"{indent}FILE: {item.Name}");
                }
            }
        } catch { }
    }
}
