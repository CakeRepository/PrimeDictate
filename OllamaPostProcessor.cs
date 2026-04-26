using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PrimeDictate;

internal sealed class OllamaPostProcessor
{
    private static readonly HttpClient HttpClient = new();
    
    public static async Task<(string ProcessedText, string SystemPrompt)> ProcessTranscriptAsync(
        string transcript, 
        string endpoint, 
        string modelName, 
        OllamaMode mode,
        ForegroundInputTarget? target, 
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return (transcript, string.Empty);
        }

        var targetTitle = target?.Title ?? string.Empty;
        var targetProcessName = string.Empty;
        
        try
        {
            if (target?.ProcessId > 0)
            {
                var process = System.Diagnostics.Process.GetProcessById((int)target.ProcessId);
                targetProcessName = process.ProcessName;
            }
        }
        catch
        {
            // Ignore process lookup failures
        }

        var systemPrompt = BuildSystemPrompt(mode, targetProcessName, targetTitle);
        
        var requestBody = new
        {
            model = modelName,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = transcript }
            },
            stream = false
        };

        try
        {
            var requestUrl = endpoint.TrimEnd('/') + "/api/chat";
            var response = await HttpClient.PostAsJsonAsync(requestUrl, requestBody, cancellationToken).ConfigureAwait(false);
            
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var result = JsonSerializer.Deserialize<JsonElement>(json);
                if (result.TryGetProperty("message", out var messageElement) && 
                    messageElement.TryGetProperty("content", out var contentElement))
                {
                    return (contentElement.GetString()?.Trim() ?? transcript, systemPrompt);
                }
            }
            else
            {
                AppLog.Error($"Ollama request failed: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to process transcript with Ollama: {ex.Message}");
        }

        // Fallback to original transcript if anything goes wrong
        return (transcript, systemPrompt);
    }

    private static string BuildSystemPrompt(OllamaMode mode, string processName, string windowTitle)
    {
        var basePrompt = mode switch
        {
            OllamaMode.Prompt => "You are an AI prompt engineer. Rewrite the user's dictated text into a clear, structured prompt for a large language model. Output ONLY the finalized prompt.",
            OllamaMode.Bug => "You are a QA engineer. Rewrite the user's dictated text into a formal bug report with steps to reproduce, expected behavior, and actual behavior. Output ONLY the bug report.",
            OllamaMode.Update => "You are a project manager. Rewrite the user's dictated text into a clear, concise status update or changelog entry. Output ONLY the finalized update text.",
            OllamaMode.Communication => "You are a professional assistant. Rewrite the user's dictated text into a professional message or email. Fix any errors and ensure a polite, clear tone. Output ONLY the message.",
            OllamaMode.Blog => "You are a content writer. Rewrite the user's dictated text into a well-written, engaging draft for a blog post. Output ONLY the blog post text.",
            OllamaMode.VibeCoding => "You are a senior software engineer. Rewrite the user's dictated text into clear, actionable, and precise instructions for an AI coding assistant. Output ONLY the instructions.",
            _ => "You are a dictation assistant. Fix any obvious grammatical, spelling, or transcription errors in the user's dictated text. Do not add conversational filler. Output ONLY the corrected text."
        };

        string contextInfo = string.Empty;
        if (!string.IsNullOrWhiteSpace(processName) || !string.IsNullOrWhiteSpace(windowTitle))
        {
            var pName = string.IsNullOrWhiteSpace(processName) ? "Unknown" : processName;
            var wTitle = string.IsNullOrWhiteSpace(windowTitle) ? "Unknown" : windowTitle;
            contextInfo = $" Context: The user is currently dictating into an application process named '{pName}' with the window title '{wTitle}'. Use this context to inform your formatting and tone, but do not mention the application name or window title in your output.";
        }

        return basePrompt + contextInfo;
    }
}
