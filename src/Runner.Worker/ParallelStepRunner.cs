using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GitHub.DistributedTask.ObjectTemplating;
using GitHub.DistributedTask.ObjectTemplating.Tokens;
using GitHub.DistributedTask.Pipelines.ContextData;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;
using System.Collections.Generic;
using GitHub.DistributedTask.WebApi;

namespace GitHub.Runner.Worker
{

    [ServiceLocator(Default = typeof(ParallelStepRunner))]
    public interface IParallelStepRunner : IStep, IRunnerService
    {
        IList<IStep> Steps { get; }
    }

    public sealed class ParallelStepRunner : RunnerService, IParallelStepRunner
    {
        public ParallelStepRunner()
        {
            
        }


        public IList<IStep> Steps
        {
            get
            {
                if (m_steps == null)
                {
                    m_steps = new List<IStep>();
                }
                return m_steps;
            }
        }

        public string Condition { get; set; }
        public TemplateToken ContinueOnError => new BooleanToken(null, null, null, false);
        public string DisplayName { get; set; }
        public IExecutionContext ExecutionContext { get; set; }
        public TemplateToken Timeout => new NumberToken(null, null, null, 0);

        private IList<IStep> m_steps;

        public async Task RunAsync()
        {
            await RunStepsAsync();
        }

        private async Task RunStepsAsync()
        {
            ArgUtil.NotNull(Steps, nameof(Steps));

            IList<Task> runningStepTasks = new List<Task>();
            Dictionary<Task, IStep> stepTasksDictionary = new Dictionary<Task, IStep>();

            foreach (IStep step in Steps)
            {
                Trace.Info($"Processing parallel step: DisplayName='{step.DisplayName}'");

                step.ExecutionContext.Start();

                step.ExecutionContext.ExpressionValues["steps"] = ExecutionContext.Global.StepsContext.GetScope(step.ExecutionContext.ScopeName);

                // Populate env context for each step
                Trace.Info("Initialize Env context for step");
#if OS_WINDOWS
                var envContext = new DictionaryContextData();
#else
                var envContext = new CaseSensitiveDictionaryContextData();
#endif

                // Global env
                foreach (var pair in ExecutionContext.Global.EnvironmentVariables)
                {
                    envContext[pair.Key] = new StringContextData(pair.Value ?? string.Empty);
                }

                // Stomps over with outside step env
                if (step.ExecutionContext.ExpressionValues.TryGetValue("env", out var envContextData))
                {
#if OS_WINDOWS
                    var dict = envContextData as DictionaryContextData;
#else
                    var dict = envContextData as CaseSensitiveDictionaryContextData;
#endif
                    foreach (var pair in dict)
                    {
                        envContext[pair.Key] = pair.Value;
                    }
                }

                step.ExecutionContext.ExpressionValues["env"] = envContext;

                var actionStep = step as IActionRunner;
                

                try
                {
                    // Evaluate and merge action's env block to env context
                    var templateEvaluator = step.ExecutionContext.ToPipelineTemplateEvaluator();
                    var actionEnvironment = templateEvaluator.EvaluateStepEnvironment(actionStep.Action.Environment, step.ExecutionContext.ExpressionValues, step.ExecutionContext.ExpressionFunctions, Common.Util.VarUtil.EnvironmentVariableKeyComparer);
                    foreach (var env in actionEnvironment)
                    {
                        envContext[env.Key] = new StringContextData(env.Value ?? string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    // fail the step since there is an evaluate error.
                    Trace.Info("Caught exception in Parallel Steps Runner from expression for step.env");
                    // evaluateStepEnvFailed = true;
                    step.ExecutionContext.Error(ex);
                    step.ExecutionContext.Complete(TaskResult.Failed);
                }
                var stepTask = RunStepAsync(step);

                runningStepTasks.Add(stepTask);
                stepTasksDictionary[stepTask] = step;
            }
            
            while (runningStepTasks.Count > 0)
            {
                Task completedTask = await Task.WhenAny(runningStepTasks);
                var completedTaskIndex = runningStepTasks.IndexOf(completedTask);
                runningStepTasks.RemoveAt(completedTaskIndex);

                var step = stepTasksDictionary[completedTask];

                CompleteStep(step);
            }
        }

        private async Task RunStepAsync(IStep step)
        {
            // Start the step.
            Trace.Info("Starting the step.");
            step.ExecutionContext.Debug($"Starting: {step.DisplayName}");

            // TODO: Fix for Step Level Timeout Attributes for an individual Composite Run Step
            // For now, we are not going to support this for an individual composite run step

            var templateEvaluator = step.ExecutionContext.ToPipelineTemplateEvaluator();

            await Common.Util.EncodingUtil.SetEncoding(HostContext, Trace, step.ExecutionContext.CancellationToken);

            try
            {
                await step.RunAsync();
            }
            catch (OperationCanceledException ex)
            {
                if (step.ExecutionContext.CancellationToken.IsCancellationRequested &&
                    !ExecutionContext.Root.CancellationToken.IsCancellationRequested)
                {
                    Trace.Error($"Caught timeout exception from step: {ex.Message}");
                    step.ExecutionContext.Error("The action has timed out.");
                    step.ExecutionContext.Result = TaskResult.Failed;
                }
                else
                {
                    Trace.Error($"Caught cancellation exception from step: {ex}");
                    step.ExecutionContext.Error(ex);
                    step.ExecutionContext.Result = TaskResult.Canceled;
                }
            }
            catch (Exception ex)
            {
                // Log the error and fail the step.
                Trace.Error($"Caught exception from step: {ex}");
                step.ExecutionContext.Error(ex);
                step.ExecutionContext.Result = TaskResult.Failed;
            }

            // Merge execution context result with command result
            if (step.ExecutionContext.CommandResult != null)
            {
                step.ExecutionContext.Result = Common.Util.TaskResultUtil.MergeTaskResults(step.ExecutionContext.Result, step.ExecutionContext.CommandResult.Value);
            }

            Trace.Info($"Step result: {step.ExecutionContext.Result}");

            // Complete the step context.
            step.ExecutionContext.Debug($"Finishing: {step.DisplayName}");
        }

        private void CompleteStep(IStep step, TaskResult? result = null, string resultCode = null)
        {
            var executionContext = step.ExecutionContext;

            executionContext.Complete(result, resultCode: resultCode);
        }
    }
}
