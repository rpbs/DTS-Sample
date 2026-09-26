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

const string LongRunningAgentInstructions = """
    You are an agent that receives a task description from the user and confirms that you
    are starting to work on it. Reply with a short acknowledgement message that repeats back
    what task you are about to execute.
    """;

AIAgent longRunningAgent = client.GetChatClient(deploymentName).AsAIAgent(LongRunningAgentInstructions, "Long Running Agent");

static async Task<string> RunLongRunningOrchestratorAsync(TaskOrchestrationContext context, string input)
{
    DurableAIAgent longRunningAgentInstance = context.GetAgent("Long Running Agent");
    var session = await longRunningAgentInstance.CreateSessionAsync();

    AgentResponse acknowledgement = await longRunningAgentInstance.RunAsync(input, session);

    // Simulates a long-running unit of work (e.g. a slow external call or batch job)
    // that takes 20 seconds to complete.
    await context.CallActivityAsync(nameof(SimulateLongRunningWork));

    return $"{acknowledgement}\n\nWork finished after 20 seconds: \"{input}\" is done.";
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
                    .AddAIAgent(longRunningAgent);
            },
            workerBuilder: builder =>
            {
                builder.UseDurableTaskScheduler(dtsConnectionString);
                builder.AddTasks(registry =>
                {
                    registry.AddOrchestratorFunc<string, string>(nameof(RunLongRunningOrchestratorAsync), RunLongRunningOrchestratorAsync);
                    registry.AddActivityFunc(nameof(SimulateLongRunningWork), SimulateLongRunningWork);
                });
            },
            clientBuilder: builder => builder.UseDurableTaskScheduler(dtsConnectionString));
    })
    .Build();

await host.StartAsync();

DurableTaskClient durableTaskClient = host.Services.GetRequiredService<DurableTaskClient>();

Console.WriteLine("Digite a descrição de uma tarefa para simular uma execução longa (20s) \n\n");

while (true)
{
    var input = Console.ReadLine();

    var instanceId = await durableTaskClient.ScheduleNewOrchestrationInstanceAsync(nameof(RunLongRunningOrchestratorAsync), input);

    Console.WriteLine($"Scheduled orchestration with ID: {instanceId} \n\n");

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

async Task SimulateLongRunningWork(TaskActivityContext context)
{
    await Task.Delay(TimeSpan.FromSeconds(20));
}
