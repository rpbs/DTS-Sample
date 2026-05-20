using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

/**
 *  INÍCIO - COISAS RELACIONADAS A CONFIGURAÇÕES DE AMBIENTE, NÃO LIGUE PRA ISSO AGORA  
 * */

string endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!;
string deploymentName = Environment.GetEnvironmentVariable("AZURE_FOUNDRY_PROJECT_DEPLOYMENT_NAME")!;
string dtsConnectionString = Environment.GetEnvironmentVariable("DURABLE_TASK_SCHEDULER_CONNECTION_STRING")!;
string azureOpenAiKey = Environment.GetEnvironmentVariable("AZURE_API_KEY")!;

AzureOpenAIClient client = !string.IsNullOrEmpty(azureOpenAiKey)
    ? new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(azureOpenAiKey))
    : new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
/**
 *  FIM - COISAS RELACIONADAS A CONFIGURAÇÕES DE AMBIENTE, NÃO LIGUE PRA ISSO AGORA  
 * */


const string PortugueseAgentInstructions = """
    You are a helpful assistant that translates English to Portuguese. You will be given a sentence in English, and you will respond with the translation in Portuguese.
""";

const string SpanishAgentInstructions = """
    You are a helpful assistant that translates English to Spanish. You will be given a sentence in English, and you will respond with the translation in Spanish.
""";

const string ItalianAgentInstructions = """
    You are a helpful assistant that translates English to Italian. You will be given a sentence in English, and you will respond with the translation in Italian.
""";

AIAgent portugueseAgent = client.GetChatClient(deploymentName).AsAIAgent(PortugueseAgentInstructions, "Portuguese Agent");
AIAgent spanishAgent = client.GetChatClient(deploymentName).AsAIAgent(SpanishAgentInstructions, "Spanish Agent");
AIAgent italianAgent = client.GetChatClient(deploymentName).AsAIAgent(ItalianAgentInstructions, "Italian Agent");

// 
static async Task<string> RunOrchestratorAsync(TaskOrchestrationContext context, string input)
{

    DurableAIAgent portugueseAgentInstance = context.GetAgent("Portuguese Agent");
    DurableAIAgent spanishAgentInstance = context.GetAgent("Spanish Agent");
    DurableAIAgent italianAgentInstance = context.GetAgent("Italian Agent");

    Task<AgentResponse> portugueseResult = portugueseAgentInstance.RunAsync(input);
    Task<AgentResponse> spanishResult = spanishAgentInstance.RunAsync(input);
    Task<AgentResponse> italianResult = italianAgentInstance.RunAsync(input);

    await Task.WhenAll(portugueseResult, spanishResult, italianResult);

    AgentResponse ptTranslation = await portugueseResult;
    AgentResponse esTranslation = await spanishResult;
    AgentResponse itTranslation = await italianResult;


    string finalResult = $"Original: {input}\nPortuguese: {ptTranslation.Text}\nSpanish: {esTranslation.Text}\nItalian: {itTranslation.Text}";

    return finalResult;
}

// Configure the console app to host the AI agents.
IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(loggingBuilder => loggingBuilder.SetMinimumLevel(LogLevel.Warning))
    .ConfigureServices(services =>
    {
        services.ConfigureDurableAgents(
            options =>
            {
                options
                    .AddAIAgent(portugueseAgent)
                    .AddAIAgent(spanishAgent)
                    .AddAIAgent(italianAgent);
            },
            workerBuilder: builder =>
            {
                builder.UseDurableTaskScheduler(dtsConnectionString);
                builder.AddTasks(registry =>
                {
                    registry.AddOrchestratorFunc<string, string>("TranslationOrchestrator", RunOrchestratorAsync);
                });
            },
            clientBuilder: builder => builder.UseDurableTaskScheduler(dtsConnectionString));
    })
    .Build();

await host.StartAsync();

DurableTaskClient durableTaskClient = host.Services.GetRequiredService<DurableTaskClient>();


while (true)
{
    var input = Console.ReadLine();

    var instanceId = await durableTaskClient.ScheduleNewOrchestrationInstanceAsync("TranslationOrchestrator", input);

    Console.WriteLine($"Scheduled orchestration with ID: {instanceId}");

    var result = await durableTaskClient.WaitForInstanceCompletionAsync(instanceId, getInputsAndOutputs: true);

    if (result.RuntimeStatus == OrchestrationRuntimeStatus.Failed)
    {
        Console.WriteLine($"Error");
    }
    else if (result.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
    {
        Console.WriteLine($"Orchestration result: {result.ReadOutputAs<string>()}");
    }
}
