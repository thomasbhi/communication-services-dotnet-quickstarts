using Azure;
using Azure.AI.OpenAI;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CognitiveServices.Speech;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

//Get ACS Connection String from appsettings.json
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

//Call Automation Client
Uri pmaEndpoint = new UriBuilder("https://uswc-01.sdf.pma.teams.microsoft.com:6448").Uri;
var client = new CallAutomationClient(pmaEndpoint, connectionString: acsConnectionString);

//Grab the Cognitive Services endpoint from appsettings.json
//var cognitiveServicesEndpoint = builder.Configuration.GetValue<string>("CognitiveServiceEndpoint");
///ArgumentNullException.ThrowIfNullOrEmpty(cognitiveServicesEndpoint);

string answerPromptSystemTemplate = """ 
    You're an AI assistant for an elevator company called Contoso Elevators. Customers will contact you as the first point of contact when having issues with their elevators. 
    Your priority is to ensure the person contacting you or anyone else in or around the elevator is safe, if not then they should contact their local authorities.
    If everyone is safe then ask the user for information about the elevators location, such as city, building and elevator number.
    Also get the users name and number so that a technician who goes onsite can contact this person. Confirm with the user all the information 
    they've shared that it's all correct and then let them know that you've created a ticket and that a technician should be onsite within the next 24 to 48 hours.
    """;

string helloPrompt = "Hello, thank you for calling! How can I help you today?";
string timeoutSilencePrompt = "I�m sorry, I didn�t hear anything. If you need assistance please let me know how I can help you.";
string goodbyePrompt = "Thank you for calling! I hope I was able to assist you. Have a great day!";
string connectAgentPrompt = "I'm sorry, I was not able to assist you with your request. Let me transfer you to an agent who can help you further. Please hold the line and I'll connect you shortly.";
string callTransferFailurePrompt = "It looks like all I can�t connect you to an agent right now, but we will get the next available agent to call you back as soon as possible.";
string agentPhoneNumberEmptyPrompt = "I�m sorry, we're currently experiencing high call volumes and all of our agents are currently busy. Our next available agent will call you back as soon as possible.";
string EndCallPhraseToConnectAgent = "Sure, please stay on the line. I�m going to transfer you to an agent.";

string transferFailedContext = "TransferFailed";
string connectAgentContext = "ConnectAgent";
string goodbyeContext = "Goodbye";

string agentPhonenumber = builder.Configuration.GetValue<string>("AgentPhoneNumber");
string chatResponseExtractPattern = @"\s*Content:(.*)\s*Score:(.*\d+)\s*Intent:(.*)\s*Category:(.*)";

var key = builder.Configuration.GetValue<string>("AzureOpenAIServiceKey");
ArgumentNullException.ThrowIfNullOrEmpty(key);

var endpoint = builder.Configuration.GetValue<string>("AzureOpenAIServiceEndpoint");
ArgumentNullException.ThrowIfNullOrEmpty(endpoint);

var speechconfig = SpeechConfig.FromSubscription(builder.Configuration.GetValue<string>("YourAzureSubscriptionKey"), builder.Configuration.GetValue<string>("YourRegion"));
// Set the output format to raw PCM
speechconfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm);

OpenAIClient ai_client = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));

//Register and make CallAutomationClient accessible via dependency injection
builder.Services.AddSingleton(client);
builder.Services.AddSingleton(ai_client);
builder.Services.AddSingleton(speechconfig);
builder.Services.AddSingleton<WebSocketHandlerService>();

var app = builder.Build();

var devTunnelUri = builder.Configuration.GetValue<string>("DevTunnelUri");
ArgumentNullException.ThrowIfNullOrEmpty(devTunnelUri);

var transportUrl = devTunnelUri.Replace("https", "wss") + "ws";

var maxTimeout = 2;

app.MapGet("/", () => "Hello ACS CallAutomation!");

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation($"Incoming Call event received.");

        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var callerId = Helper.GetCallerId(jsonObject);
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
        var callbackUri = new Uri(new Uri(devTunnelUri), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
        Console.WriteLine($"Callback Url: {callbackUri}");

        MediaStreamingOptions mediaStreamingOptions = new MediaStreamingOptions(new Uri(transportUrl),
                MediaStreamingContent.Audio, MediaStreamingAudioChannel.Mixed, startMediaStreaming: true);
      
        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
           // CallIntelligenceOptions = new CallIntelligenceOptions() { CognitiveServicesEndpoint = new Uri(cognitiveServicesEndpoint) },
            MediaStreamingOptions = mediaStreamingOptions,
        };

        AnswerCallResult answerCallResult = await client.AnswerCallAsync(options);
        Console.WriteLine($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");

        //Use EventProcessor to process CallConnected event
        var answer_result = await answerCallResult.WaitForEventProcessorAsync();
        if (answer_result.IsSuccess)
        {
            Console.WriteLine($"Call connected event received for CorrelationId id: {answer_result.SuccessResult.CorrelationId}");
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();
            //await HandleRecognizeAsync(callConnectionMedia, callerId, helloPrompt);
            //await SendChatCompletionsStreamingAsync("Hello, how are you?");
        }

        client.GetEventProcessor().AttachOngoingEventProcessor<PlayCompleted>(answerCallResult.CallConnection.CallConnectionId, async (playCompletedEvent) =>
        {
            logger.LogInformation($"Play completed event received for connection id: {playCompletedEvent.CallConnectionId}.");
            if (!string.IsNullOrWhiteSpace(playCompletedEvent.OperationContext) && (playCompletedEvent.OperationContext.Equals(transferFailedContext, StringComparison.OrdinalIgnoreCase) 
            || playCompletedEvent.OperationContext.Equals(goodbyeContext, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogInformation($"Disconnecting the call...");
                await answerCallResult.CallConnection.HangUpAsync(true);
            }
            else if (!string.IsNullOrWhiteSpace(playCompletedEvent.OperationContext) && playCompletedEvent.OperationContext.Equals(connectAgentContext, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(agentPhonenumber))
                {
                    logger.LogInformation($"Agent phone number is empty");
                    await HandlePlayAsync(agentPhoneNumberEmptyPrompt,
                      transferFailedContext, answerCallResult.CallConnection.GetCallMedia());
                }
                else
                {
                    logger.LogInformation($"Initializing the Call transfer...");
                    CommunicationIdentifier transferDestination = new PhoneNumberIdentifier(agentPhonenumber);
                    TransferCallToParticipantResult result = await answerCallResult.CallConnection.TransferCallToParticipantAsync(transferDestination);
                    logger.LogInformation($"Transfer call initiated: {result.OperationContext}");
                }
            }
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<PlayFailed>(answerCallResult.CallConnection.CallConnectionId, async (playFailedEvent) =>
        {
            logger.LogInformation($"Play failed event received for connection id: {playFailedEvent.CallConnectionId}. Hanging up call...");
            await answerCallResult.CallConnection.HangUpAsync(true);
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferAccepted>(answerCallResult.CallConnection.CallConnectionId, async (callTransferAcceptedEvent) =>
        {
            logger.LogInformation($"Call transfer accepted event received for connection id: {callTransferAcceptedEvent.CallConnectionId}.");
        });
        client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferFailed>(answerCallResult.CallConnection.CallConnectionId, async (callTransferFailedEvent) =>
        {
            logger.LogInformation($"Call transfer failed event received for connection id: {callTransferFailedEvent.CallConnectionId}.");
            var resultInformation = callTransferFailedEvent.ResultInformation;
            logger.LogError("Encountered error during call transfer, message={msg}, code={code}, subCode={subCode}", resultInformation?.Message, resultInformation?.Code, resultInformation?.SubCode);

            await HandlePlayAsync(callTransferFailurePrompt,
                       transferFailedContext, answerCallResult.CallConnection.GetCallMedia());

        });
        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeCompleted>(answerCallResult.CallConnection.CallConnectionId, async (recognizeCompletedEvent) =>
        {
            Console.WriteLine($"Recognize completed event received for connection id: {recognizeCompletedEvent.CallConnectionId}");
            var speech_result = recognizeCompletedEvent.RecognizeResult as SpeechResult;
            if (!string.IsNullOrWhiteSpace(speech_result?.Speech))
            {
                Console.WriteLine($"Recognized speech: {speech_result.Speech}");

                if (await DetectEscalateToAgentIntent(speech_result.Speech, logger))
                {
                    await HandlePlayAsync(EndCallPhraseToConnectAgent,
                               connectAgentContext, answerCallResult.CallConnection.GetCallMedia());
                }
                else
                {
                    var chatGPTResponse = await GetChatGPTResponse(speech_result.Speech);
                    logger.LogInformation($"Chat GPT response: {chatGPTResponse}");
                    Regex regex = new Regex(chatResponseExtractPattern);
                    Match match = regex.Match(chatGPTResponse);
                    if (match.Success)
                    {
                        string answer = match.Groups[1].Value;
                        string sentimentScore = match.Groups[2].Value.Trim();
                        string intent = match.Groups[3].Value;
                        string category = match.Groups[4].Value;

                        logger.LogInformation("Chat GPT Answer={ans}, Sentiment Rating={rating}, Intent={Int}, Category={cat}",
                            answer, sentimentScore, intent, category);
                        var score = getSentimentScore(sentimentScore);
                        if (score > -1 && score < 5)
                        {
                            await HandlePlayAsync(connectAgentPrompt,
                                connectAgentContext, answerCallResult.CallConnection.GetCallMedia());
                        }
                        else
                        {
                            await HandleChatResponse(answer, answerCallResult.CallConnection.GetCallMedia(), callerId, logger);
                        }
                    }
                    else
                    {
                        logger.LogInformation("No match found");
                        await HandleChatResponse(chatGPTResponse, answerCallResult.CallConnection.GetCallMedia(), callerId, logger);
                    }
                }
            }
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeFailed>(answerCallResult.CallConnection.CallConnectionId, async (recognizeFailedEvent) =>
        {
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();

            if (MediaEventReasonCode.RecognizeInitialSilenceTimedOut.Equals(recognizeFailedEvent.ResultInformation.SubCode.Value.ToString()) && maxTimeout > 0)
            {
                Console.WriteLine($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Retrying recognize...");
                maxTimeout--;
                await HandleRecognizeAsync(callConnectionMedia, callerId, timeoutSilencePrompt);
            }
            else
            {
                Console.WriteLine($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Playing goodbye message...");
                await HandlePlayAsync(goodbyePrompt, goodbyeContext, callConnectionMedia);
            }
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<MediaStreamingStopped>(
                answerCallResult.CallConnection.CallConnectionId, async (mediaStreamingStopped) =>
                {
                    logger.LogInformation("Received media streaming event: {type}", mediaStreamingStopped.GetType());
                });
        
        client.GetEventProcessor().AttachOngoingEventProcessor<MediaStreamingFailed>(
            answerCallResult.CallConnection.CallConnectionId, async (mediaStreamingFailed) =>
            {
                logger.LogInformation($"Received media streaming event: {mediaStreamingFailed.GetType()}, " +
                    $"SubCode: {mediaStreamingFailed?.ResultInformation?.SubCode}, Message: {mediaStreamingFailed?.ResultInformation?.Message}");
            });
        
        client.GetEventProcessor().AttachOngoingEventProcessor<MediaStreamingStarted>(
            answerCallResult.CallConnection.CallConnectionId, async (mediaStreamingStartedEvent) =>
        {
            Console.WriteLine($"MediaStreaming started event received for connection id: {mediaStreamingStartedEvent.CallConnectionId}");
        });

    }
    return Results.Ok();
});

// api to handle call back events
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    CallAutomationClient callAutomationClient,
    ILogger<Program> logger) =>
{
    var eventProcessor = client.GetEventProcessor();
    eventProcessor.ProcessEvents(cloudEvents);
    return Results.Ok();
});

app.UseWebSockets();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            var mediaService = context.RequestServices.GetRequiredService<WebSocketHandlerService>();

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            mediaService.SetConnection(webSocket);

            // Set the single WebSocket connection
            var openAiModelName = builder.Configuration.GetValue<string>("AzureOpenAIDeploymentModelName");
            await mediaService.ProcessWebSocketAsync(endpoint, key, openAiModelName);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    else
    {
        await next(context);
    }
});

async Task HandleChatResponse(string chatResponse, CallMedia callConnectionMedia, string callerId, ILogger logger, string context = "OpenAISample")
{
    var chatGPTResponseSource = new TextSource(chatResponse)
    {
        VoiceName = "en-US-NancyNeural"
    };

    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(callerId))
        {
            InterruptPrompt = false,
            InitialSilenceTimeout = TimeSpan.FromSeconds(15),
            Prompt = chatGPTResponseSource,
            OperationContext = context,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(500)
        };

    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

int getSentimentScore(string sentimentScore)
{
    string pattern = @"(\d)+";
    Regex regex = new Regex(pattern);
    Match match = regex.Match(sentimentScore);
    return match.Success ? int.Parse(match.Value) : -1;
}

async Task<bool> DetectEscalateToAgentIntent(string speechText, ILogger logger) =>
           await HasIntentAsync(userQuery: speechText, intentDescription: "talk to agent", logger);

async Task<bool> HasIntentAsync(string userQuery, string intentDescription, ILogger logger)
{
    var systemPrompt = "You are a helpful assistant";
    var baseUserPrompt = "In 1 word: does {0} have similar meaning as {1}?";
    var combinedPrompt = string.Format(baseUserPrompt, userQuery, intentDescription);

    var response = await GetChatCompletionsAsync(systemPrompt, combinedPrompt);

    var isMatch = response.ToLowerInvariant().Contains("yes");
    logger.LogInformation($"OpenAI results: isMatch={isMatch}, customerQuery='{userQuery}', intentDescription='{intentDescription}'");
    return isMatch;
}

async Task<string> GetChatGPTResponse(string speech_input)
{
    return await GetChatCompletionsAsync(answerPromptSystemTemplate, speech_input);
}

async Task SendChatCompletionsStreamingAsync(string userPrompt)
{
    var chatCompletionsOptions = new ChatCompletionsOptions()
    {
        Messages = {
                   // new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt),
                    },
        MaxTokens = 1000
    };

    var webSocketHandlerService = app.Services.GetRequiredService<WebSocketHandlerService>();

   // await webSocketHandlerService.GetOpenAiStreamResponseAsync(builder.Configuration.GetValue<string>("AzureOpenAIDeploymentModelName"), chatCompletionsOptions);
}

async Task<string> GetChatCompletionsAsync(string systemPrompt, string userPrompt)
{
    var chatCompletionsOptions = new ChatCompletionsOptions()
    {
        Messages = {
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt),
                    },
        MaxTokens = 1000
    };

    var response = await ai_client.GetChatCompletionsAsync(
        deploymentOrModelName: builder.Configuration.GetValue<string>("AzureOpenAIDeploymentModelName"),
        chatCompletionsOptions);

    var response_content = response.Value.Choices[0].Message.Content;
    return response_content;
}

async Task HandleRecognizeAsync(CallMedia callConnectionMedia, string callerId, string message)
{
    // Play greeting message
    var greetingPlaySource = new TextSource(message)
    {
        VoiceName = "en-US-NancyNeural"
    };

    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(callerId))
        {
            InterruptPrompt = false,
            InitialSilenceTimeout = TimeSpan.FromSeconds(15),
            Prompt = greetingPlaySource,
            OperationContext = "GetFreeFormText",
            EndSilenceTimeout = TimeSpan.FromMilliseconds(500)
        };

    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

async Task HandlePlayAsync(string textToPlay, string context, CallMedia callConnectionMedia)
{
    // Play message
    var playSource = new TextSource(textToPlay)
    {
        VoiceName = "en-US-NancyNeural"
    };

    var playOptions = new PlayToAllOptions(playSource) { OperationContext = context };
    await callConnectionMedia.PlayToAllAsync(playOptions);
}

app.Run();