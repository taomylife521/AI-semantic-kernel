﻿// Copyright (c) Microsoft. All rights reserved.

using System.Collections.ObjectModel;
using System.Diagnostics;
using Azure.AI.Agents.Persistent;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.OpenAI;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI.Assistants;
using OpenAI.Files;
using ChatTokenUsage = OpenAI.Chat.ChatTokenUsage;
using UsageDetails = Microsoft.Extensions.AI.UsageDetails;

/// <summary>
/// Base class for samples that demonstrate the usage of host agents
/// based on API's such as Open AI Assistants or Azure AI Agents.
/// </summary>
public abstract class BaseAgentsTest<TClient>(ITestOutputHelper output) : BaseAgentsTest(output)
{
    /// <summary>
    /// Gets the root client for the service.
    /// </summary>
    protected abstract TClient Client { get; }
}

/// <summary>
/// Base class for samples that demonstrate the usage of agents.
/// </summary>
public abstract class BaseAgentsTest(ITestOutputHelper output) : BaseTest(output, redirectSystemConsoleOutput: true)
{
    /// <summary>
    /// Metadata key to indicate the assistant as created for a sample.
    /// </summary>
    protected const string SampleMetadataKey = "sksample";

    /// <summary>
    /// Metadata to indicate the object was created for a sample.
    /// </summary>
    /// <remarks>
    /// While the samples do attempt delete the objects it creates, it is possible
    /// that some may remain.  This metadata can be used to identify and sample
    /// objects for manual clean-up.
    /// </remarks>
    protected static readonly ReadOnlyDictionary<string, string> SampleMetadata =
        new(new Dictionary<string, string>
        {
            { SampleMetadataKey, bool.TrueString }
        });

    protected (string? pluginName, string functionName) ParseFunctionName(string functionName)
    {
        string[] parts = functionName.Split("-", 2);
        if (parts.Length == 1)
        {
            return (null, parts[0]);
        }
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Common method to write formatted agent chat content to the console.
    /// </summary>
    protected void WriteAgentChatMessage(ChatMessageContent message)
    {
        // Include ChatMessageContent.AuthorName in output, if present.
        string authorExpression = message.Role == AuthorRole.User ? string.Empty : FormatAuthor();
        // Include TextContent (via ChatMessageContent.Content), if present.
        string contentExpression = string.IsNullOrWhiteSpace(message.Content) ? string.Empty : message.Content;
        bool isCode = message.Metadata?.ContainsKey(OpenAIAssistantAgent.CodeInterpreterMetadataKey) ?? false;
        string codeMarker = isCode ? "\n  [CODE]\n" : " ";
        System.Console.WriteLine($"\n# {message.Role}{authorExpression}:{codeMarker}{contentExpression}");

        // Provide visibility for inner content (that isn't TextContent).
        foreach (KernelContent item in message.Items)
        {
            if (item is AnnotationContent annotation)
            {
                if (annotation.Kind == AnnotationKind.UrlCitation)
                {
                    Console.WriteLine($"  [{item.GetType().Name}] {annotation.Label}: {annotation.ReferenceId} - {annotation.Title}");
                }
                else
                {
                    Console.WriteLine($"  [{item.GetType().Name}] {annotation.Label}: File #{annotation.ReferenceId}");
                }
            }
            else if (item is ActionContent action)
            {
                Console.WriteLine($"  [{item.GetType().Name}] {action.Text}");
            }
            else if (item is ReasoningContent reasoning)
            {
                Console.WriteLine($"  [{item.GetType().Name}] {reasoning.Text.DefaultIfEmpty("Thinking...")}");
            }
            else if (item is FileReferenceContent fileReference)
            {
                Console.WriteLine($"  [{item.GetType().Name}] File #{fileReference.FileId}");
            }
            else if (item is ImageContent image)
            {
                Console.WriteLine($"  [{item.GetType().Name}] {image.Uri?.ToString() ?? image.DataUri ?? $"{image.Data?.Length} bytes"}");
            }
            else if (item is FunctionCallContent functionCall)
            {
                Console.WriteLine($"  [{item.GetType().Name}] {functionCall.Id}");
            }
            else if (item is FunctionResultContent functionResult)
            {
                Console.WriteLine($"  [{item.GetType().Name}] {functionResult.CallId} - {functionResult.Result?.AsJson() ?? "*"}");
            }
        }

        if (message.Metadata?.TryGetValue("Usage", out object? usage) ?? false)
        {
            if (usage is RunStepTokenUsage assistantUsage)
            {
                WriteUsage(assistantUsage.TotalTokenCount, assistantUsage.InputTokenCount, assistantUsage.OutputTokenCount);
            }
            else if (usage is RunStepCompletionUsage agentUsage)
            {
                WriteUsage(agentUsage.TotalTokens, agentUsage.PromptTokens, agentUsage.CompletionTokens);
            }
            else if (usage is ChatTokenUsage chatUsage)
            {
                WriteUsage(chatUsage.TotalTokenCount, chatUsage.InputTokenCount, chatUsage.OutputTokenCount);
            }
            else if (usage is UsageDetails usageDetails)
            {
                WriteUsage(usageDetails.TotalTokenCount ?? 0, usageDetails.InputTokenCount ?? 0, usageDetails.OutputTokenCount ?? 0);
            }
        }

        string FormatAuthor() => message.AuthorName is not null ? $" - {message.AuthorName ?? " * "}" : string.Empty;

        void WriteUsage(long totalTokens, long inputTokens, long outputTokens)
        {
            Console.WriteLine($"  [Usage] Tokens: {totalTokens}, Input: {inputTokens}, Output: {outputTokens}");
        }
    }

    /// <summary>
    /// Common method to write formatted agent streaming chat content to the console.
    /// </summary>
    protected async Task<AgentThread?> WriteAgentStreamMessageAsync(IAsyncEnumerable<AgentResponseItem<StreamingChatMessageContent>> responseItems)
    {
        var first = true;
        AgentThread? thread = null;
        await foreach (var responseItem in responseItems)
        {
            var message = responseItem.Message;
            if (first)
            {
                Console.Write($"# {message.AuthorName ?? message.Role.ToString()}> ");
                first = false;
            }
            Console.Write(message.Content);
            thread = responseItem.Thread;
        }
        Console.WriteLine();

        return thread;
    }

    protected async Task DownloadResponseContentAsync(OpenAIFileClient client, ChatMessageContent message)
    {
        foreach (KernelContent item in message.Items)
        {
            if (item is AnnotationContent annotation)
            {
                await this.DownloadFileContentAsync(client, annotation.ReferenceId!);
            }
        }
    }

    protected async Task DownloadResponseImageAsync(OpenAIFileClient client, ChatMessageContent message)
    {
        foreach (KernelContent item in message.Items)
        {
            if (item is FileReferenceContent fileReference)
            {
                await this.DownloadFileContentAsync(client, fileReference.FileId, launchViewer: true);
            }
        }
    }

    private async Task DownloadFileContentAsync(OpenAIFileClient client, string fileId, bool launchViewer = false)
    {
        OpenAIFile fileInfo = client.GetFile(fileId);
        if (fileInfo.Purpose == FilePurpose.AssistantsOutput)
        {
            string filePath = Path.Combine(Path.GetTempPath(), Path.GetFileName(fileInfo.Filename));
            if (launchViewer)
            {
                filePath = Path.ChangeExtension(filePath, ".png");
            }

            BinaryData content = await client.DownloadFileAsync(fileId);
            File.WriteAllBytes(filePath, content.ToArray());
            Console.WriteLine($"  File #{fileId} saved to: {filePath}");

            if (launchViewer)
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/C start {filePath}"
                    });
            }
        }
    }
}
