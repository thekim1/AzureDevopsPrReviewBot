# PR Review Bot

An AI-powered pull request review tool for **Azure DevOps** that uses **Anthropic Claude** to automatically analyze code changes and provide actionable feedback directly in your terminal.

![.NET](https://img.shields.io/badge/.NET-10.0-blueviolet) ![Claude](https://img.shields.io/badge/Claude-Anthropic-orange) ![Azure DevOps](https://img.shields.io/badge/Azure-DevOps-blue)

---

## Features

- 🔍 **Fetches PRs assigned to you** across all repositories in an Azure DevOps project
- 🤖 **AI-powered review** using Claude — identifies bugs, security issues, performance problems, and bad patterns
- 🎛️ **Interactive selection** — choose one or multiple PRs to review in a single run
- 📊 **Severity-rated comments** — Critical 🔴, Warning 🟡, Info 🔵
- 💬 **Post comments back** to Azure DevOps with a single confirmation
- 💾 **Saves reviews to disk** as markdown files for later reference
- 🖥️ **Rich terminal UI** powered by Spectre.Console

---

## Prerequisites

| Requirement | Notes |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Runtime and build toolchain |
| Azure DevOps account | With access to the target project |
| Azure DevOps PAT | Personal Access Token with `Code (Read)` and `Code (Write)` scopes |
| [Anthropic API Key](https://console.anthropic.com/) | Claude API access |

---

## Getting Started

### 1. Clone the repository

```bash
git clone https://github.com/thekim1/AzureDevopsPrReviewBot.git
cd AzureDevopsPrReviewBot
```

### 2. Configure settings

Open `PrReviewBot/appsettings.json` and fill in your values:

```json
{
  "AzureDevOps": {
    "OrganizationUrl": "https://dev.azure.com/YOUR_ORG",
    "Project": "YOUR_PROJECT",
    "PersonalAccessToken": "YOUR_PAT_HERE",
    "ReviewerEmail": "you@example.com"
  },
  "Claude": {
    "ApiKey": "YOUR_ANTHROPIC_API_KEY",
    "Model": "claude-sonnet-4-6"
  }
}
```

> ⚠️ **Do not commit secrets.** Use [.NET User Secrets](#using-net-user-secrets-recommended) or environment variables instead.

### 3. Build and run

```bash
cd PrReviewBot
dotnet run
```

---

## Configuration

The app loads configuration from the following sources in order (later sources override earlier ones):

1. `appsettings.json`
2. [.NET User Secrets](#using-net-user-secrets-recommended)
3. Environment variables

### Configuration Reference

| Key | Description |
|---|---|
| `AzureDevOps:OrganizationUrl` | Your Azure DevOps org URL, e.g. `https://dev.azure.com/myorg` |
| `AzureDevOps:Project` | The Azure DevOps project name |
| `AzureDevOps:PersonalAccessToken` | PAT with `Code (Read)` + `Code (Write)` scopes |
| `AzureDevOps:ReviewerEmail` | Your email — used to identify PRs assigned to you |
| `Claude:ApiKey` | Your Anthropic API key |
| `Claude:Model` | Claude model to use (default: `claude-sonnet-4-6`) |

### Using .NET User Secrets (Recommended)

Keeps secrets out of source control:

```bash
cd PrReviewBot
dotnet user-secrets init
dotnet user-secrets set "AzureDevOps:PersonalAccessToken" "YOUR_PAT"
dotnet user-secrets set "Claude:ApiKey" "YOUR_ANTHROPIC_API_KEY"
```

### Using Environment Variables

```bash
# Windows (PowerShell)
$env:AzureDevOps__PersonalAccessToken = "YOUR_PAT"
$env:Claude__ApiKey = "YOUR_ANTHROPIC_API_KEY"

# Linux / macOS
export AzureDevOps__PersonalAccessToken="YOUR_PAT"
export Claude__ApiKey="YOUR_ANTHROPIC_API_KEY"
```

> Note: Use double underscores (`__`) as the separator for nested keys in environment variables.

---

## How It Works

```
┌─────────────────────────────────────────────────────────────┐
│  1. Connect to Azure DevOps and fetch active PRs assigned   │
│     to you across all repositories in the project           │
│                                                             │
│  2. Select which PR(s) to review from an interactive list   │
│                                                             │
│  3. PR diffs are sent to Claude for analysis                │
│                                                             │
│  4. Review results are displayed in the terminal with       │
│     severity ratings and suggested code fixes               │
│                                                             │
│  5. Optionally post comments directly to the PR in          │
│     Azure DevOps                                            │
│                                                             │
│  6. Review is saved to a local file in /reviews/            │
└─────────────────────────────────────────────────────────────┘
```

### Severity Levels

| Severity | Icon | When Used |
|---|---|---|
| **Critical** | 🔴 | Security vulnerabilities, data loss, crashes, serious bugs |
| **Warning** | 🟡 | Performance problems, bad patterns, maintainability issues |
| **Info** | 🔵 | Style improvements, minor suggestions |

---

## Project Structure

```
PrReviewBot/
├── Config/
│   └── AppSettings.cs          # Strongly-typed configuration classes
├── Models/
│   ├── PullRequestInfo.cs      # PR data model
│   └── ReviewComment.cs        # Review comment + severity enum
├── Services/
│   ├── AzureDevOpsService.cs   # Azure DevOps API integration
│   ├── ClaudeReviewService.cs  # Anthropic Claude AI integration
│   └── ReviewOutputService.cs  # Terminal display + file output
├── Program.cs                  # Entry point + interactive CLI flow
└── appsettings.json            # Configuration file
```

---

## Azure DevOps PAT Setup

1. Go to **Azure DevOps → User Settings → Personal Access Tokens**
2. Click **New Token**
3. Set an expiration date and select the following scopes:
   - **Code** → `Read`
   - **Code** → `Write` *(only needed if you want to post comments)*
4. Copy the generated token into your configuration

---

## Anthropic API Key Setup

1. Sign in at [console.anthropic.com](https://console.anthropic.com/)
2. Navigate to **API Keys** and create a new key
3. Copy the key into your configuration

---

## Dependencies

| Package | Purpose |
|---|---|
| `Anthropic` | Official Anthropic SDK for Claude API |
| `Microsoft.TeamFoundationServer.Client` | Azure DevOps REST API client |
| `Microsoft.VisualStudio.Services.Client` | Azure DevOps authentication & connection |
| `Spectre.Console` | Rich terminal UI (colors, spinners, panels) |
| `Microsoft.Extensions.Configuration.*` | JSON + env var + user secrets config |
