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
using System.Text.Json.Serialization;

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

const string invitationAgent = """
    You are agent that invites people to my party. 
    You need to ask the user for their name. 
    In case the user don't provide a clear name, 
    return a JSON response with a "success" property set to false and a "message" containing a
    message indicating the issue, otherwise return a JSON response with a "success" property set to true and a "message" containing a confirmation message.
    """;

AIAgent invitationAgentInstance = client.GetChatClient(deploymentName).AsAIAgent(invitationAgent, "Invitation Agent");

static async Task<string> RunInvitationOrchestratorAsync(TaskOrchestrationContext context, string input)
{

    DurableAIAgent invitationAgentInstance = context.GetAgent("Invitation Agent");
    var session = await invitationAgentInstance.CreateSessionAsync();

    AgentResponse<Result> invitationResult = await invitationAgentInstance.RunAsync<Result>(input, session);

    if (!invitationResult.Result.Success)
    {
        return await context.CallActivityAsync<string>(nameof(InvalidUserName), invitationResult.Result.Message);
    }

    return await context.CallActivityAsync<string>(nameof(InvitationSuccess), invitationResult.Result.Message);
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
                    .AddAIAgent(invitationAgentInstance);
            },
            workerBuilder: builder =>
            {
                builder.UseDurableTaskScheduler(dtsConnectionString);
                builder.AddTasks(registry =>
                {
                    registry.AddOrchestratorFunc<string>(nameof(RunInvitationOrchestratorAsync), RunInvitationOrchestratorAsync);
                    registry.AddActivityFunc<string>(nameof(InvalidUserName), InvalidUserName);
                    registry.AddActivityFunc<string>(nameof(InvitationSuccess), InvitationSuccess);
                });
            },
            clientBuilder: builder => builder.UseDurableTaskScheduler(dtsConnectionString));
    })
    .Build();

await host.StartAsync();

DurableTaskClient durableTaskClient = host.Services.GetRequiredService<DurableTaskClient>();

Console.WriteLine("Digite o nome da pessoa para convidar \n\n");

while (true)
{
    var input = Console.ReadLine();

    var instanceId = await durableTaskClient.ScheduleNewOrchestrationInstanceAsync(nameof(RunInvitationOrchestratorAsync), input);

    Console.WriteLine($"Scheduled orchestration with ID: {instanceId} \n\n");

    await durableTaskClient.WaitForInstanceCompletionAsync(instanceId, getInputsAndOutputs: true);
}



void InvalidUserName(TaskActivityContext context, string message)
{
    Console.WriteLine($"Invalid: {message} \n\n");
}

void InvitationSuccess(TaskActivityContext context, string message)
{
    Console.WriteLine($"Success: {message}");
}

public sealed record Result
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}