﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Moq;
using OpenAI.Chat;

namespace SemanticKernel.Connectors.AzureOpenAI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="AzureOpenAIChatCompletionService"/>
/// </summary>
public sealed class AzureOpenAIChatCompletionServiceTests : IDisposable
{
    private readonly MultipleHttpMessageHandlerStub _messageHandlerStub;
    private readonly HttpClient _httpClient;
    private readonly Mock<ILoggerFactory> _mockLoggerFactory;

    public AzureOpenAIChatCompletionServiceTests()
    {
        this._messageHandlerStub = new MultipleHttpMessageHandlerStub();
        this._httpClient = new HttpClient(this._messageHandlerStub, false);
        this._mockLoggerFactory = new Mock<ILoggerFactory>();

        var mockLogger = new Mock<ILogger>();

        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        this._mockLoggerFactory.Setup(l => l.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConstructorWithApiKeyWorksCorrectly(bool includeLoggerFactory)
    {
        // Arrange & Act
        var service = includeLoggerFactory ?
            new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", loggerFactory: this._mockLoggerFactory.Object) :
            new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id");

        // Assert
        Assert.NotNull(service);
        Assert.Equal("model-id", service.Attributes["ModelId"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConstructorWithTokenCredentialWorksCorrectly(bool includeLoggerFactory)
    {
        // Arrange & Act
        var credentials = DelegatedTokenCredential.Create((_, _) => new AccessToken());
        var service = includeLoggerFactory ?
            new AzureOpenAIChatCompletionService("deployment", "https://endpoint", credentials, "model-id", loggerFactory: this._mockLoggerFactory.Object) :
            new AzureOpenAIChatCompletionService("deployment", "https://endpoint", credentials, "model-id");

        // Assert
        Assert.NotNull(service);
        Assert.Equal("model-id", service.Attributes["ModelId"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConstructorWithOpenAIClientWorksCorrectly(bool includeLoggerFactory)
    {
        // Arrange & Act
        var client = new AzureOpenAIClient(new Uri("http://host"), "key");
        var service = includeLoggerFactory ?
            new AzureOpenAIChatCompletionService("deployment", client, "model-id", loggerFactory: this._mockLoggerFactory.Object) :
            new AzureOpenAIChatCompletionService("deployment", client, "model-id");

        // Assert
        Assert.NotNull(service);
        Assert.Equal("model-id", service.Attributes["ModelId"]);
    }

    [Fact]
    public async Task GetTextContentsWorksCorrectlyAsync()
    {
        // Arrange
        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);
        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_test_response.json"))
        });

        // Act
        var result = await service.GetTextContentsAsync("Prompt");

        // Assert
        Assert.True(result.Count > 0);
        Assert.Equal("Test chat response", result[0].Text);

        var usage = result[0].Metadata?["Usage"] as ChatTokenUsage;

        Assert.NotNull(usage);
        Assert.Equal(55, usage.InputTokens);
        Assert.Equal(100, usage.OutputTokens);
        Assert.Equal(155, usage.TotalTokens);
    }

    [Fact]
    public async Task GetChatMessageContentsHandlesSettingsCorrectlyAsync()
    {
        // Arrange
        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);
        var settings = new AzureOpenAIPromptExecutionSettings()
        {
            MaxTokens = 123,
            Temperature = 0.6,
            TopP = 0.5,
            FrequencyPenalty = 1.6,
            PresencePenalty = 1.2,
            Seed = 567,
            TokenSelectionBiases = new Dictionary<int, int> { { 2, 3 } },
            StopSequences = ["stop_sequence"],
            Logprobs = true,
            TopLogprobs = 5,
            AzureChatDataSource = new AzureSearchChatDataSource()
            {
                Endpoint = new Uri("http://test-search-endpoint"),
                IndexName = "test-index-name",
                Authentication = DataSourceAuthentication.FromApiKey("api-key"),
            }
        };

        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage("User Message");
        chatHistory.AddUserMessage([new ImageContent(new Uri("https://image")), new TextContent("User Message")]);
        chatHistory.AddSystemMessage("System Message");
        chatHistory.AddAssistantMessage("Assistant Message");

        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_test_response.json"))
        });

        // Act
        var result = await service.GetChatMessageContentsAsync(chatHistory, settings);

        // Assert
        var requestContent = this._messageHandlerStub.RequestContents[0];

        Assert.NotNull(requestContent);

        var content = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(requestContent));

        var messages = content.GetProperty("messages");

        var userMessage = messages[0];
        var userMessageCollection = messages[1];
        var systemMessage = messages[2];
        var assistantMessage = messages[3];

        Assert.Equal("user", userMessage.GetProperty("role").GetString());
        Assert.Equal("User Message", userMessage.GetProperty("content").GetString());

        Assert.Equal("user", userMessageCollection.GetProperty("role").GetString());
        var contentItems = userMessageCollection.GetProperty("content");
        Assert.Equal(2, contentItems.GetArrayLength());
        Assert.Equal("https://image/", contentItems[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("image_url", contentItems[0].GetProperty("type").GetString());
        Assert.Equal("User Message", contentItems[1].GetProperty("text").GetString());
        Assert.Equal("text", contentItems[1].GetProperty("type").GetString());

        Assert.Equal("system", systemMessage.GetProperty("role").GetString());
        Assert.Equal("System Message", systemMessage.GetProperty("content").GetString());

        Assert.Equal("assistant", assistantMessage.GetProperty("role").GetString());
        Assert.Equal("Assistant Message", assistantMessage.GetProperty("content").GetString());

        Assert.Equal(123, content.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.6, content.GetProperty("temperature").GetDouble());
        Assert.Equal(0.5, content.GetProperty("top_p").GetDouble());
        Assert.Equal(1.6, content.GetProperty("frequency_penalty").GetDouble());
        Assert.Equal(1.2, content.GetProperty("presence_penalty").GetDouble());
        Assert.Equal(567, content.GetProperty("seed").GetInt32());
        Assert.Equal(3, content.GetProperty("logit_bias").GetProperty("2").GetInt32());
        Assert.Equal("stop_sequence", content.GetProperty("stop")[0].GetString());
        Assert.True(content.GetProperty("logprobs").GetBoolean());
        Assert.Equal(5, content.GetProperty("top_logprobs").GetInt32());

        var dataSources = content.GetProperty("data_sources");
        Assert.Equal(1, dataSources.GetArrayLength());
        Assert.Equal("azure_search", dataSources[0].GetProperty("type").GetString());

        var dataSourceParameters = dataSources[0].GetProperty("parameters");
        Assert.Equal("http://test-search-endpoint/", dataSourceParameters.GetProperty("endpoint").GetString());
        Assert.Equal("test-index-name", dataSourceParameters.GetProperty("index_name").GetString());
    }

    [Theory]
    [MemberData(nameof(ResponseFormats))]
    public async Task GetChatMessageContentsHandlesResponseFormatCorrectlyAsync(object responseFormat, string? expectedResponseType)
    {
        // Arrange
        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);
        var settings = new AzureOpenAIPromptExecutionSettings
        {
            ResponseFormat = responseFormat
        };

        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_test_response.json"))
        });

        // Act
        var result = await service.GetChatMessageContentsAsync(new ChatHistory("System message"), settings);

        // Assert
        var requestContent = this._messageHandlerStub.RequestContents[0];

        Assert.NotNull(requestContent);

        var content = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(requestContent));

        Assert.Equal(expectedResponseType, content.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Theory]
    [MemberData(nameof(ToolCallBehaviors))]
    public async Task GetChatMessageContentsWorksCorrectlyAsync(AzureOpenAIToolCallBehavior behavior)
    {
        // Arrange
        var kernel = Kernel.CreateBuilder().Build();
        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);
        var settings = new AzureOpenAIPromptExecutionSettings() { ToolCallBehavior = behavior };

        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_test_response.json"))
        });

        // Act
        var result = await service.GetChatMessageContentsAsync(new ChatHistory("System message"), settings, kernel);

        // Assert
        Assert.True(result.Count > 0);
        Assert.Equal("Test chat response", result[0].Content);

        var usage = result[0].Metadata?["Usage"] as ChatTokenUsage;

        Assert.NotNull(usage);
        Assert.Equal(55, usage.InputTokens);
        Assert.Equal(100, usage.OutputTokens);
        Assert.Equal(155, usage.TotalTokens);

        Assert.Equal("Stop", result[0].Metadata?["FinishReason"]);
    }

    [Fact]
    public async Task GetChatMessageContentsWithFunctionCallAsync()
    {
        // Arrange
        int functionCallCount = 0;

        var kernel = Kernel.CreateBuilder().Build();
        var function1 = KernelFunctionFactory.CreateFromMethod((string location) =>
        {
            functionCallCount++;
            return "Some weather";
        }, "GetCurrentWeather");

        var function2 = KernelFunctionFactory.CreateFromMethod((string argument) =>
        {
            functionCallCount++;
            throw new ArgumentException("Some exception");
        }, "FunctionWithException");

        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("MyPlugin", [function1, function2]));

        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient, this._mockLoggerFactory.Object);
        var settings = new AzureOpenAIPromptExecutionSettings() { ToolCallBehavior = AzureOpenAIToolCallBehavior.AutoInvokeKernelFunctions };

        using var response1 = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_multiple_function_calls_test_response.json")) };
        using var response2 = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_test_response.json")) };

        this._messageHandlerStub.ResponsesToReturn = [response1, response2];

        // Act
        var result = await service.GetChatMessageContentsAsync(new ChatHistory("System message"), settings, kernel);

        // Assert
        Assert.True(result.Count > 0);
        Assert.Equal("Test chat response", result[0].Content);

        Assert.Equal(2, functionCallCount);
    }

    [Fact]
    public async Task GetChatMessageContentsWithFunctionCallMaximumAutoInvokeAttemptsAsync()
    {
        // Arrange
        const int DefaultMaximumAutoInvokeAttempts = 128;
        const int ModelResponsesCount = 129;

        int functionCallCount = 0;

        var kernel = Kernel.CreateBuilder().Build();
        var function = KernelFunctionFactory.CreateFromMethod((string location) =>
        {
            functionCallCount++;
            return "Some weather";
        }, "GetCurrentWeather");

        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("MyPlugin", [function]));

        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient, this._mockLoggerFactory.Object);
        var settings = new AzureOpenAIPromptExecutionSettings() { ToolCallBehavior = AzureOpenAIToolCallBehavior.AutoInvokeKernelFunctions };

        var responses = new List<HttpResponseMessage>();

        for (var i = 0; i < ModelResponsesCount; i++)
        {
            responses.Add(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_single_function_call_test_response.json")) });
        }

        this._messageHandlerStub.ResponsesToReturn = responses;

        // Act
        var result = await service.GetChatMessageContentsAsync(new ChatHistory("System message"), settings, kernel);

        // Assert
        Assert.Equal(DefaultMaximumAutoInvokeAttempts, functionCallCount);
    }

    [Fact]
    public async Task GetChatMessageContentsWithRequiredFunctionCallAsync()
    {
        // Arrange
        int functionCallCount = 0;

        var kernel = Kernel.CreateBuilder().Build();
        var function = KernelFunctionFactory.CreateFromMethod((string location) =>
        {
            functionCallCount++;
            return "Some weather";
        }, "GetCurrentWeather");

        var plugin = KernelPluginFactory.CreateFromFunctions("MyPlugin", [function]);
        var openAIFunction = plugin.GetFunctionsMetadata().First().ToAzureOpenAIFunction();

        kernel.Plugins.Add(plugin);

        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient, this._mockLoggerFactory.Object);
        var settings = new AzureOpenAIPromptExecutionSettings() { ToolCallBehavior = AzureOpenAIToolCallBehavior.RequireFunction(openAIFunction, autoInvoke: true) };

        using var response1 = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_single_function_call_test_response.json")) };
        using var response2 = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_test_response.json")) };

        this._messageHandlerStub.ResponsesToReturn = [response1, response2];

        // Act
        var result = await service.GetChatMessageContentsAsync(new ChatHistory("System message"), settings, kernel);

        // Assert
        Assert.Equal(1, functionCallCount);

        var requestContents = this._messageHandlerStub.RequestContents;

        Assert.Equal(2, requestContents.Count);

        requestContents.ForEach(Assert.NotNull);

        var firstContent = Encoding.UTF8.GetString(requestContents[0]!);
        var secondContent = Encoding.UTF8.GetString(requestContents[1]!);

        var firstContentJson = JsonSerializer.Deserialize<JsonElement>(firstContent);
        var secondContentJson = JsonSerializer.Deserialize<JsonElement>(secondContent);

        Assert.Equal(1, firstContentJson.GetProperty("tools").GetArrayLength());
        Assert.Equal("MyPlugin-GetCurrentWeather", firstContentJson.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());

        Assert.Equal("none", secondContentJson.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task GetStreamingTextContentsWorksCorrectlyAsync()
    {
        // Arrange
        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(AzureOpenAITestHelper.GetTestResponse("chat_completion_streaming_test_response.txt")));

        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        });

        // Act & Assert
        var enumerator = service.GetStreamingTextContentsAsync("Prompt").GetAsyncEnumerator();

        await enumerator.MoveNextAsync();
        Assert.Equal("Test chat streaming response", enumerator.Current.Text);

        await enumerator.MoveNextAsync();
        Assert.Equal("Stop", enumerator.Current.Metadata?["FinishReason"]);
    }

    [Fact]
    public async Task GetStreamingChatMessageContentsWorksCorrectlyAsync()
    {
        // Arrange
        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(AzureOpenAITestHelper.GetTestResponse("chat_completion_streaming_test_response.txt")));

        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        });

        // Act & Assert
        var enumerator = service.GetStreamingChatMessageContentsAsync([]).GetAsyncEnumerator();

        await enumerator.MoveNextAsync();
        Assert.Equal("Test chat streaming response", enumerator.Current.Content);

        await enumerator.MoveNextAsync();
        Assert.Equal("Stop", enumerator.Current.Metadata?["FinishReason"]);
    }

    [Fact]
    public async Task GetStreamingChatMessageContentsWithFunctionCallAsync()
    {
        // Arrange
        int functionCallCount = 0;

        var kernel = Kernel.CreateBuilder().Build();
        var function1 = KernelFunctionFactory.CreateFromMethod((string location) =>
        {
            functionCallCount++;
            return "Some weather";
        }, "GetCurrentWeather");

        var function2 = KernelFunctionFactory.CreateFromMethod((string argument) =>
        {
            functionCallCount++;
            throw new ArgumentException("Some exception");
        }, "FunctionWithException");

        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("MyPlugin", [function1, function2]));

        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient, this._mockLoggerFactory.Object);
        var settings = new AzureOpenAIPromptExecutionSettings() { ToolCallBehavior = AzureOpenAIToolCallBehavior.AutoInvokeKernelFunctions };

        using var response1 = new HttpResponseMessage(HttpStatusCode.OK) { Content = AzureOpenAITestHelper.GetTestResponseAsStream("chat_completion_streaming_multiple_function_calls_test_response.txt") };
        using var response2 = new HttpResponseMessage(HttpStatusCode.OK) { Content = AzureOpenAITestHelper.GetTestResponseAsStream("chat_completion_streaming_test_response.txt") };

        this._messageHandlerStub.ResponsesToReturn = [response1, response2];

        // Act & Assert
        var enumerator = service.GetStreamingChatMessageContentsAsync([], settings, kernel).GetAsyncEnumerator();

        await enumerator.MoveNextAsync();
        Assert.Equal("Test chat streaming response", enumerator.Current.Content);
        Assert.Equal("ToolCalls", enumerator.Current.Metadata?["FinishReason"]);

        await enumerator.MoveNextAsync();
        Assert.Equal("ToolCalls", enumerator.Current.Metadata?["FinishReason"]);

        // Keep looping until the end of stream
        while (await enumerator.MoveNextAsync())
        {
        }

        Assert.Equal(2, functionCallCount);
    }

    [Fact]
    public async Task GetStreamingChatMessageContentsWithFunctionCallMaximumAutoInvokeAttemptsAsync()
    {
        // Arrange
        const int DefaultMaximumAutoInvokeAttempts = 128;
        const int ModelResponsesCount = 129;

        int functionCallCount = 0;

        var kernel = Kernel.CreateBuilder().Build();
        var function = KernelFunctionFactory.CreateFromMethod((string location) =>
        {
            functionCallCount++;
            return "Some weather";
        }, "GetCurrentWeather");

        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("MyPlugin", [function]));

        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient, this._mockLoggerFactory.Object);
        var settings = new AzureOpenAIPromptExecutionSettings() { ToolCallBehavior = AzureOpenAIToolCallBehavior.AutoInvokeKernelFunctions };

        var responses = new List<HttpResponseMessage>();

        for (var i = 0; i < ModelResponsesCount; i++)
        {
            responses.Add(new HttpResponseMessage(HttpStatusCode.OK) { Content = AzureOpenAITestHelper.GetTestResponseAsStream("chat_completion_streaming_single_function_call_test_response.txt") });
        }

        this._messageHandlerStub.ResponsesToReturn = responses;

        // Act & Assert
        await foreach (var chunk in service.GetStreamingChatMessageContentsAsync([], settings, kernel))
        {
            Assert.Equal("Test chat streaming response", chunk.Content);
        }

        Assert.Equal(DefaultMaximumAutoInvokeAttempts, functionCallCount);
    }

    [Fact]
    public async Task GetStreamingChatMessageContentsWithRequiredFunctionCallAsync()
    {
        // Arrange
        int functionCallCount = 0;

        var kernel = Kernel.CreateBuilder().Build();
        var function = KernelFunctionFactory.CreateFromMethod((string location) =>
        {
            functionCallCount++;
            return "Some weather";
        }, "GetCurrentWeather");

        var plugin = KernelPluginFactory.CreateFromFunctions("MyPlugin", [function]);
        var openAIFunction = plugin.GetFunctionsMetadata().First().ToAzureOpenAIFunction();

        kernel.Plugins.Add(plugin);

        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient, this._mockLoggerFactory.Object);
        var settings = new AzureOpenAIPromptExecutionSettings() { ToolCallBehavior = AzureOpenAIToolCallBehavior.RequireFunction(openAIFunction, autoInvoke: true) };

        using var response1 = new HttpResponseMessage(HttpStatusCode.OK) { Content = AzureOpenAITestHelper.GetTestResponseAsStream("chat_completion_streaming_single_function_call_test_response.txt") };
        using var response2 = new HttpResponseMessage(HttpStatusCode.OK) { Content = AzureOpenAITestHelper.GetTestResponseAsStream("chat_completion_streaming_test_response.txt") };

        this._messageHandlerStub.ResponsesToReturn = [response1, response2];

        // Act & Assert
        var enumerator = service.GetStreamingChatMessageContentsAsync([], settings, kernel).GetAsyncEnumerator();

        // Function Tool Call Streaming (One Chunk)
        await enumerator.MoveNextAsync();
        Assert.Equal("Test chat streaming response", enumerator.Current.Content);
        Assert.Equal("ToolCalls", enumerator.Current.Metadata?["FinishReason"]);

        // Chat Completion Streaming (1st Chunk)
        await enumerator.MoveNextAsync();
        Assert.Null(enumerator.Current.Metadata?["FinishReason"]);

        // Chat Completion Streaming (2nd Chunk)
        await enumerator.MoveNextAsync();
        Assert.Equal("Stop", enumerator.Current.Metadata?["FinishReason"]);

        Assert.Equal(1, functionCallCount);

        var requestContents = this._messageHandlerStub.RequestContents;

        Assert.Equal(2, requestContents.Count);

        requestContents.ForEach(Assert.NotNull);

        var firstContent = Encoding.UTF8.GetString(requestContents[0]!);
        var secondContent = Encoding.UTF8.GetString(requestContents[1]!);

        var firstContentJson = JsonSerializer.Deserialize<JsonElement>(firstContent);
        var secondContentJson = JsonSerializer.Deserialize<JsonElement>(secondContent);

        Assert.Equal(1, firstContentJson.GetProperty("tools").GetArrayLength());
        Assert.Equal("MyPlugin-GetCurrentWeather", firstContentJson.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());

        Assert.Equal("none", secondContentJson.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task GetChatMessageContentsUsesPromptAndSettingsCorrectlyAsync()
    {
        // Arrange
        const string Prompt = "This is test prompt";
        const string SystemMessage = "This is test system message";

        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);
        var settings = new AzureOpenAIPromptExecutionSettings() { ChatSystemPrompt = SystemMessage };

        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_test_response.json"))
        });

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddTransient<IChatCompletionService>((sp) => service);
        Kernel kernel = builder.Build();

        // Act
        var result = await kernel.InvokePromptAsync(Prompt, new(settings));

        // Assert
        Assert.Equal("Test chat response", result.ToString());

        var requestContentByteArray = this._messageHandlerStub.RequestContents[0];

        Assert.NotNull(requestContentByteArray);

        var requestContent = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(requestContentByteArray));

        var messages = requestContent.GetProperty("messages");

        Assert.Equal(2, messages.GetArrayLength());

        Assert.Equal(SystemMessage, messages[0].GetProperty("content").GetString());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());

        Assert.Equal(Prompt, messages[1].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task GetChatMessageContentsWithChatMessageContentItemCollectionAndSettingsCorrectlyAsync()
    {
        // Arrange
        const string Prompt = "This is test prompt";
        const string SystemMessage = "This is test system message";
        const string AssistantMessage = "This is assistant message";
        const string CollectionItemPrompt = "This is collection item prompt";

        var service = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);
        var settings = new AzureOpenAIPromptExecutionSettings() { ChatSystemPrompt = SystemMessage };

        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_test_response.json"))
        });

        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage(Prompt);
        chatHistory.AddAssistantMessage(AssistantMessage);
        chatHistory.AddUserMessage(
        [
            new TextContent(CollectionItemPrompt),
            new ImageContent(new Uri("https://image"))
        ]);

        // Act
        var result = await service.GetChatMessageContentsAsync(chatHistory, settings);

        // Assert
        Assert.True(result.Count > 0);
        Assert.Equal("Test chat response", result[0].Content);

        var requestContentByteArray = this._messageHandlerStub.RequestContents[0];

        Assert.NotNull(requestContentByteArray);

        var requestContent = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(requestContentByteArray));

        var messages = requestContent.GetProperty("messages");

        Assert.Equal(4, messages.GetArrayLength());

        Assert.Equal(SystemMessage, messages[0].GetProperty("content").GetString());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());

        Assert.Equal(Prompt, messages[1].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());

        Assert.Equal(AssistantMessage, messages[2].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());

        var contentItems = messages[3].GetProperty("content");
        Assert.Equal(2, contentItems.GetArrayLength());
        Assert.Equal(CollectionItemPrompt, contentItems[0].GetProperty("text").GetString());
        Assert.Equal("text", contentItems[0].GetProperty("type").GetString());
        Assert.Equal("https://image/", contentItems[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("image_url", contentItems[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task FunctionCallsShouldBePropagatedToCallersViaChatMessageItemsOfTypeFunctionCallContentAsync()
    {
        // Arrange
        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_multiple_function_calls_test_response.json"))
        });

        var sut = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);

        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage("Fake prompt");

        var settings = new AzureOpenAIPromptExecutionSettings() { ToolCallBehavior = AzureOpenAIToolCallBehavior.EnableKernelFunctions };

        // Act
        var result = await sut.GetChatMessageContentAsync(chatHistory, settings);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(5, result.Items.Count);

        var getCurrentWeatherFunctionCall = result.Items[0] as FunctionCallContent;
        Assert.NotNull(getCurrentWeatherFunctionCall);
        Assert.Equal("GetCurrentWeather", getCurrentWeatherFunctionCall.FunctionName);
        Assert.Equal("MyPlugin", getCurrentWeatherFunctionCall.PluginName);
        Assert.Equal("1", getCurrentWeatherFunctionCall.Id);
        Assert.Equal("Boston, MA", getCurrentWeatherFunctionCall.Arguments?["location"]?.ToString());

        var functionWithExceptionFunctionCall = result.Items[1] as FunctionCallContent;
        Assert.NotNull(functionWithExceptionFunctionCall);
        Assert.Equal("FunctionWithException", functionWithExceptionFunctionCall.FunctionName);
        Assert.Equal("MyPlugin", functionWithExceptionFunctionCall.PluginName);
        Assert.Equal("2", functionWithExceptionFunctionCall.Id);
        Assert.Equal("value", functionWithExceptionFunctionCall.Arguments?["argument"]?.ToString());

        var nonExistentFunctionCall = result.Items[2] as FunctionCallContent;
        Assert.NotNull(nonExistentFunctionCall);
        Assert.Equal("NonExistentFunction", nonExistentFunctionCall.FunctionName);
        Assert.Equal("MyPlugin", nonExistentFunctionCall.PluginName);
        Assert.Equal("3", nonExistentFunctionCall.Id);
        Assert.Equal("value", nonExistentFunctionCall.Arguments?["argument"]?.ToString());

        var invalidArgumentsFunctionCall = result.Items[3] as FunctionCallContent;
        Assert.NotNull(invalidArgumentsFunctionCall);
        Assert.Equal("InvalidArguments", invalidArgumentsFunctionCall.FunctionName);
        Assert.Equal("MyPlugin", invalidArgumentsFunctionCall.PluginName);
        Assert.Equal("4", invalidArgumentsFunctionCall.Id);
        Assert.Null(invalidArgumentsFunctionCall.Arguments);
        Assert.NotNull(invalidArgumentsFunctionCall.Exception);
        Assert.Equal("Error: Function call arguments were invalid JSON.", invalidArgumentsFunctionCall.Exception.Message);
        Assert.NotNull(invalidArgumentsFunctionCall.Exception.InnerException);

        var intArgumentsFunctionCall = result.Items[4] as FunctionCallContent;
        Assert.NotNull(intArgumentsFunctionCall);
        Assert.Equal("IntArguments", intArgumentsFunctionCall.FunctionName);
        Assert.Equal("MyPlugin", intArgumentsFunctionCall.PluginName);
        Assert.Equal("5", intArgumentsFunctionCall.Id);
        Assert.Equal("36", intArgumentsFunctionCall.Arguments?["age"]?.ToString());
    }

    [Fact]
    public async Task FunctionCallsShouldBeReturnedToLLMAsync()
    {
        // Arrange
        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_test_response.json"))
        });

        var sut = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);

        var items = new ChatMessageContentItemCollection
        {
            new FunctionCallContent("GetCurrentWeather", "MyPlugin", "1", new KernelArguments() { ["location"] = "Boston, MA" }),
            new FunctionCallContent("GetWeatherForecast", "MyPlugin", "2", new KernelArguments() { ["location"] = "Boston, MA" })
        };

        ChatHistory chatHistory =
        [
            new ChatMessageContent(AuthorRole.Assistant, items)
        ];

        var settings = new AzureOpenAIPromptExecutionSettings() { ToolCallBehavior = AzureOpenAIToolCallBehavior.EnableKernelFunctions };

        // Act
        await sut.GetChatMessageContentAsync(chatHistory, settings);

        // Assert
        var actualRequestContent = Encoding.UTF8.GetString(this._messageHandlerStub.RequestContents[0]!);
        Assert.NotNull(actualRequestContent);

        var optionsJson = JsonSerializer.Deserialize<JsonElement>(actualRequestContent);

        var messages = optionsJson.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());

        var assistantMessage = messages[0];
        Assert.Equal("assistant", assistantMessage.GetProperty("role").GetString());

        Assert.Equal(2, assistantMessage.GetProperty("tool_calls").GetArrayLength());

        var tool1 = assistantMessage.GetProperty("tool_calls")[0];
        Assert.Equal("1", tool1.GetProperty("id").GetString());
        Assert.Equal("function", tool1.GetProperty("type").GetString());

        var function1 = tool1.GetProperty("function");
        Assert.Equal("MyPlugin-GetCurrentWeather", function1.GetProperty("name").GetString());
        Assert.Equal("{\"location\":\"Boston, MA\"}", function1.GetProperty("arguments").GetString());

        var tool2 = assistantMessage.GetProperty("tool_calls")[1];
        Assert.Equal("2", tool2.GetProperty("id").GetString());
        Assert.Equal("function", tool2.GetProperty("type").GetString());

        var function2 = tool2.GetProperty("function");
        Assert.Equal("MyPlugin-GetWeatherForecast", function2.GetProperty("name").GetString());
        Assert.Equal("{\"location\":\"Boston, MA\"}", function2.GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task FunctionResultsCanBeProvidedToLLMAsOneResultPerChatMessageAsync()
    {
        // Arrange
        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_test_response.json"))
        });

        var sut = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);

        var chatHistory = new ChatHistory
        {
            new ChatMessageContent(AuthorRole.Tool,
            [
                new FunctionResultContent(new FunctionCallContent("GetCurrentWeather", "MyPlugin", "1", new KernelArguments() { ["location"] = "Boston, MA" }), "rainy"),
            ]),
            new ChatMessageContent(AuthorRole.Tool,
            [
                new FunctionResultContent(new FunctionCallContent("GetWeatherForecast", "MyPlugin", "2", new KernelArguments() { ["location"] = "Boston, MA" }), "sunny")
            ])
        };

        var settings = new AzureOpenAIPromptExecutionSettings() { ToolCallBehavior = AzureOpenAIToolCallBehavior.EnableKernelFunctions };

        // Act
        await sut.GetChatMessageContentAsync(chatHistory, settings);

        // Assert
        var actualRequestContent = Encoding.UTF8.GetString(this._messageHandlerStub.RequestContents[0]!);
        Assert.NotNull(actualRequestContent);

        var optionsJson = JsonSerializer.Deserialize<JsonElement>(actualRequestContent);

        var messages = optionsJson.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());

        var assistantMessage = messages[0];
        Assert.Equal("tool", assistantMessage.GetProperty("role").GetString());
        Assert.Equal("rainy", assistantMessage.GetProperty("content").GetString());
        Assert.Equal("1", assistantMessage.GetProperty("tool_call_id").GetString());

        var assistantMessage2 = messages[1];
        Assert.Equal("tool", assistantMessage2.GetProperty("role").GetString());
        Assert.Equal("sunny", assistantMessage2.GetProperty("content").GetString());
        Assert.Equal("2", assistantMessage2.GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task FunctionResultsCanBeProvidedToLLMAsManyResultsInOneChatMessageAsync()
    {
        // Arrange
        this._messageHandlerStub.ResponsesToReturn.Add(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AzureOpenAITestHelper.GetTestResponse("chat_completion_test_response.json"))
        });

        var sut = new AzureOpenAIChatCompletionService("deployment", "https://endpoint", "api-key", "model-id", this._httpClient);

        var chatHistory = new ChatHistory
        {
            new ChatMessageContent(AuthorRole.Tool,
            [
                new FunctionResultContent(new FunctionCallContent("GetCurrentWeather", "MyPlugin", "1", new KernelArguments() { ["location"] = "Boston, MA" }), "rainy"),
                new FunctionResultContent(new FunctionCallContent("GetWeatherForecast", "MyPlugin", "2", new KernelArguments() { ["location"] = "Boston, MA" }), "sunny")
            ])
        };

        var settings = new AzureOpenAIPromptExecutionSettings() { ToolCallBehavior = AzureOpenAIToolCallBehavior.EnableKernelFunctions };

        // Act
        await sut.GetChatMessageContentAsync(chatHistory, settings);

        // Assert
        var actualRequestContent = Encoding.UTF8.GetString(this._messageHandlerStub.RequestContents[0]!);
        Assert.NotNull(actualRequestContent);

        var optionsJson = JsonSerializer.Deserialize<JsonElement>(actualRequestContent);

        var messages = optionsJson.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());

        var assistantMessage = messages[0];
        Assert.Equal("tool", assistantMessage.GetProperty("role").GetString());
        Assert.Equal("rainy", assistantMessage.GetProperty("content").GetString());
        Assert.Equal("1", assistantMessage.GetProperty("tool_call_id").GetString());

        var assistantMessage2 = messages[1];
        Assert.Equal("tool", assistantMessage2.GetProperty("role").GetString());
        Assert.Equal("sunny", assistantMessage2.GetProperty("content").GetString());
        Assert.Equal("2", assistantMessage2.GetProperty("tool_call_id").GetString());
    }

    public void Dispose()
    {
        this._httpClient.Dispose();
        this._messageHandlerStub.Dispose();
    }

    public static TheoryData<AzureOpenAIToolCallBehavior> ToolCallBehaviors => new()
    {
        AzureOpenAIToolCallBehavior.EnableKernelFunctions,
        AzureOpenAIToolCallBehavior.AutoInvokeKernelFunctions
    };

    public static TheoryData<object, string?> ResponseFormats => new()
    {
        { "json_object", "json_object" },
        { "text", "text" }
    };
}