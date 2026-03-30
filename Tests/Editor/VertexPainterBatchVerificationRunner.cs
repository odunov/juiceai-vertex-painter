using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace JuiceAI.VertexPainter.Tests.Editor
{
    public static class VertexPainterBatchVerificationRunner
    {
        private const string AssemblyName = "JuiceAI.VertexPainter.Tests.Editor";
        private static readonly string ResultsPath = Path.Combine(Path.GetTempPath(), "VertexPainterBatchResults.txt");

        private static TestRunnerApi runnerApi;
        private static BatchCallbacks callbacks;

        public static void Run()
        {
            runnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            callbacks = new BatchCallbacks(ResultsPath);
            runnerApi.RegisterCallbacks(callbacks);

            ExecutionSettings settings = new(new Filter
            {
                assemblyNames = new[] { AssemblyName },
                testMode = TestMode.EditMode
            });

            runnerApi.Execute(settings);
        }

        private sealed class BatchCallbacks : ICallbacks
        {
            private readonly string resultsPath;

            public BatchCallbacks(string resultsPath)
            {
                this.resultsPath = resultsPath;
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                Debug.Log($"Vertex painter verification started for assembly '{AssemblyName}'.");
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                StringBuilder summary = new();
                summary.AppendLine("Vertex Painter Batch Verification");
                summary.AppendLine($"Assembly: {AssemblyName}");
                summary.AppendLine($"Passed: {result.PassCount}");
                summary.AppendLine($"Failed: {result.FailCount}");
                summary.AppendLine($"Skipped: {result.SkipCount}");
                summary.AppendLine($"Inconclusive: {result.InconclusiveCount}");
                summary.AppendLine($"Duration: {result.Duration:0.###}s");

                Directory.CreateDirectory(Path.GetDirectoryName(resultsPath) ?? ".");
                File.WriteAllText(resultsPath, summary.ToString());

                EditorApplication.Exit(result.FailCount > 0 ? 1 : 0);
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
            }
        }
    }
}
