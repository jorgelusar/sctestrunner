﻿namespace NUnitContrib.Web.TestRunner.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Web;
    using NUnit.Core;
    using NUnit.Core.Filters;
    using NUnit.Util;
    using Configuration;
    using Core;

    public class RunnerHandler : BaseHttpHandler, EventListener
    {

        private static readonly TestRunnerSection testRunnerConfig =
            ConfigurationManager.GetSection("testrunner") as TestRunnerSection;

        private readonly NUnitWebRunner nunitWebRunner;

        private readonly List<NUnitTestMethod> tests = new List<NUnitTestMethod>();
        private readonly List<string> categories = new List<string>();
        private readonly List<TestResult> testResults = new List<TestResult>();
        private readonly StringBuilder textOutputBuilder = new StringBuilder();
        private readonly StringBuilder consoleBuilder = new StringBuilder();

        private readonly List<string> assemblyList;
        private readonly TestPackage package;
        private readonly string testResultPath;

        private SimpleTestRunner runner;
        private StringWriter consoleStringWriter;
        private int totalTests;

        public RunnerHandler()
        {
            if (string.IsNullOrEmpty(testRunnerConfig.RoutePath))
                throw new ConfigurationErrorsException("You must configure a route path.");

            if (testRunnerConfig.Assemblies == null || testRunnerConfig.Assemblies.Count == 0)
                throw new ConfigurationErrorsException("You must configure at least one assembly.");

            var codeBase = Assembly.GetExecutingAssembly().CodeBase;
            var directoryName = Path.GetDirectoryName(codeBase);
            if (directoryName == null) throw new DirectoryNotFoundException("Unable to determine running location for test runner");

            // Ensure the assembly path exists
            var currentDirectory = new Uri(directoryName).LocalPath;

            assemblyList = new List<string>();
            foreach (AssemblyElement testAssembly in testRunnerConfig.Assemblies)
            {
                var assemblypath = Path.GetFullPath(Path.Combine(currentDirectory, testAssembly.Name + ".dll"));
                if (!File.Exists(assemblypath)) throw new FileNotFoundException("Cannot find test assembly at " + assemblypath);
                assemblyList.Add(assemblypath);
            }

            // Get the test result path
            if (!string.IsNullOrEmpty(testRunnerConfig.ResultPath))
            {
                testResultPath = Path.GetFullPath(Path.Combine(currentDirectory, testRunnerConfig.ResultPath));
            }

            // Initialize NUnit
            if (!CoreExtensions.Host.Initialized) CoreExtensions.Host.InitializeService();
            package = new TestPackage(prefix, assemblyList);
            var testSuite = new TestSuiteBuilder().Build(package);

            // Recursively load all tests
            Action<ITest> getTests = null;
            getTests = x =>
                x.Tests.Cast<ITest>().ToList().ForEach(t =>
                {
                    t.Categories.Cast<string>().ToList().ForEach(c =>
                    {
                        if (!categories.Contains(c)) categories.Add(c);
                    });

                    var item = t as NUnitTestMethod;
                    if (item != null) tests.Add(item);
                    if (t.IsSuite) getTests(t);
                });

            getTests(testSuite);
            nunitWebRunner = new NUnitWebRunner(assemblyList, testResultPath);
        }

        public override void ProcessRequest(HttpContextBase context)
        {
            var path = context.Request.Path;
            var fileName = Path.GetFileName(path);
            if (fileName == null)
            {
                NotFound(context);
                return;
            }

            var file = fileName.ToLowerInvariant();
            if (testRunnerConfig.RoutePath.Equals(file))
            {
                // If the users come to /testrunner instead of /testrunner/
                // the assets links would be absolute so they couldn't be
                // loaded.
                context.Response.Redirect("/" + testRunnerConfig.RoutePath + "/");
                return;
            }

            nunitWebRunner.Session = context.Session;

            switch (file)
            {
                case "":
                    ReturnResource(context, "index.html", "text/html");
                    break;
                case "results.html":
                case "list.html":
                    ReturnResource(context, file, "text/html");
                    break;
                case "angular.js":
                case "angular-route.js":
                case "app.js":
                case "bootstrap.js":
                case "controllers.js":
                case "jquery.js":
                    ReturnResource(context, file, "application/javascript");
                    break;
                case "app.css":
                case "bootstrap.css":
                    ReturnResource(context, file, "text/css");
                    break;
                case "glyphicons-halflings-regular.eot":
                    ReturnResource(context, file, "application/vnd.ms-fontobject");
                    break;
                case "glyphicons-halflings-regular.svg":
                    ReturnResource(context, file, "image/svg+xml");
                    break;
                case "glyphicons-halflings-regular.ttf":
                    ReturnResource(context, file, "application/octet-stream");
                    break;
                case "glyphicons-halflings-regular.woff":
                    ReturnResource(context, file, "application/font-woff");
                    break;
                case "glyphicons-halflings-regular.woff2":
                    ReturnResource(context, file, "application/font-woff2");
                    break;
                case "gettestsuite.json":
                    ReturnJson(context, nunitWebRunner.GetTestSuiteConfigInfo());
                    break;
                case "gettests.json":
                    ReturnJson(context, nunitWebRunner.GetTestSuiteInfo());
                    break;
                case "getrunnerstatus.json":
                    ReturnJson(context, GetRunnerStatus());
                    break;
                case "runfixture.json":
                    ReturnJson(context, RunFixture(context.Request["name"]));
                    break;
                case "runtest.json":
                    ReturnJson(context, RunTest(context.Request["id"]));
                    break;
                case "runcategories.json":
                    ReturnJson(context, RunCategories(context.Request["name"]));
                    break;
                case "runtests.json":
                    ReturnJson(context, RunTests());
                    break;
                case "cancel.json":
                    ReturnJson(context, Cancel());
                    break;
                default:
                    NotFound(context);
                    break;
            }
        }

        private object GetTestResult(List<TestResult> results)
        {
            Func<TestResult, string> getStatus = r =>
            {
                switch (r.ResultState)
                {
                    case ResultState.Success:
                        return "success";
                    case ResultState.Error:
                    case ResultState.Failure:
                        return "danger";
                    default:
                        return "warning";
                }
            };

            // Get the parameters
            var passed = results.Count(t => t.ResultState == ResultState.Success);
            var failed = results.Count(t => t.ResultState == ResultState.Failure);
            var errors = results.Count(t => t.ResultState == ResultState.Error);
            var inconclusive = results.Count(t => t.ResultState == ResultState.Inconclusive);
            var invalid = results.Count(t => t.ResultState == ResultState.NotRunnable);
            var ignored = results.Count(t => t.ResultState == ResultState.Ignored);
            var skipped = results.Count(t => t.ResultState == ResultState.Skipped);
            var time = results.Sum(t => t.Time);

            // Get the status
            string status;
            if (passed == results.Count) status = "success";
            else if (failed + errors > 0) status = "danger";
            else status = "warning";

            // Get the message
            var text = String.Format("Passed {0}, Failed {1}, Errors {2}, Inconclusive {3}, Invalid {4}, Ignored {5}, Skipped {6}, Time {7}",
                passed, failed, errors, inconclusive, invalid, ignored, skipped, time);
            var message = new { text, status };

            // Get the test results
            var list = results
                .Select(r => new
                {
                    id = r.Test.TestName.TestID.ToString(),
                    name = r.Test.MethodName,
                    fixture = r.Test.ClassName,
                    description = r.Test.Description,
                    message = String.Concat(r.Message, r.IsError ? "\r\n" + r.StackTrace : string.Empty),
                    status = getStatus(r)
                })
                .ToArray();

            var fixtures = list.GroupBy(x => x.fixture).Select(g => new { name = g.Key, tests = g.ToArray() });
            var errorlist = list.Where(x => x.status == "danger").GroupBy(x => x.fixture).Select(g => new { name = g.Key, tests = g.ToArray() });
            var ignoredlist = list.Where(x => x.status == "warning").GroupBy(x => x.fixture).Select(g => new { name = g.Key, tests = g.ToArray() });
            var textoutput = textOutputBuilder.ToString().TrimEnd();

            return new { message, fixtures, errorlist, ignoredlist, textoutput };
        }

        private object RunTests(ITestFilter filter)
        {
            if (runner != null && runner.Running)
            {
                while (runner.Running) { /*do nothing*/ }
                return GetTestResult(testResults);
            }

            using (runner = new SimpleTestRunner())
            {
                runner.Load(package);
                if (runner.Test == null)
                {
                    runner.Unload();
                    return new {text = "Unable to load the tests", status = "warning"};
                }

                TestResult result;
                try
                {
                    result = runner.Run(this, filter, true, LoggingThreshold.All);
                }
                catch (Exception e)
                {
                    return new {text = e.Message, status = "error"};
                }

                return result == null 
                    ? new {text = "No results", status = "warning"} 
                    : GetTestResult(testResults);
            }
        }

        private object RunFixture(string name)
        {
            var invalid = new { text = "Please select a valid fixture", status = "info" };
            if (String.IsNullOrWhiteSpace(name)) return new { message = invalid };

            if (!tests.Any(t => t.ClassName.Equals(name))) return new { message = invalid };

            var namesToAdd = tests
                .Where(t => t.ClassName.Equals(name))
                .OrderBy(t => t.TestName.TestID.ToString())
                .Select(t => t.TestName.FullName)
                .ToArray();

            var simpleNameFilter = new SimpleNameFilter(namesToAdd);
            return RunTests(simpleNameFilter);
        }

        private object RunTest(string id)
        {
            var invalid = new { text = "Please select a valid test", status = "info" };
            if (String.IsNullOrWhiteSpace(id)) return new { message = invalid };
            
            var test = tests.FirstOrDefault(t => t.TestName.FullName.Equals(id));
            if (test == null) return new { message = invalid };

            var simpleNameFilter = new SimpleNameFilter(id);
            return RunTests(simpleNameFilter);
        }

        private object RunCategories(string name)
        {
            var invalid = new { text = "Please select a valid category/categories", status = "info" };
            if (String.IsNullOrWhiteSpace(name)) return new { message = invalid };

            var ctgrs = name.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (ctgrs.Length == 0) return new { message = invalid };

            var validCtgrs = categories.Intersect(ctgrs.Select(HttpUtility.UrlDecode)).ToArray();
            if (!validCtgrs.Any()) return new { message = invalid };

            var categoryFilter = new CategoryFilter(validCtgrs);
            return RunTests(categoryFilter);
        }

        private object RunTests()
        {
            var invalid = new { text = "There are not any test to run", status = "info" };
            return tests.Count == 0 ? new { message = invalid } : RunTests(TestFilter.Empty);
        }

        private object GetRunnerStatus()
        {
            var active = runner != null && runner.Running;
            if (totalTests.Equals(0)) return new { counter = 0, active };
            var copy = new List<TestResult>(testResults);
            var counter = (copy.Count * 100) / totalTests;
            return new { counter, active };
        }

        private object Cancel()
        {
            if (runner != null && runner.Running)
            {
                runner.CancelRun();
            }

            return new { text = "Runner cancelled", status = "warning" };
        }

        #region EventListener

        public void RunStarted(string name, int testCount)
        {
            // Clear the results. The list will be populated when TestFinished is called
            testResults.Clear();

            // Intercept the console write
            textOutputBuilder.Clear();
            consoleBuilder.Clear();
            consoleStringWriter = new StringWriter(consoleBuilder);
            Console.SetOut(consoleStringWriter); 

            // Set the test count
            totalTests = testCount;
        }

        public void RunFinished(TestResult result)
        {
            // Reset console output
            consoleStringWriter.Dispose();
            var consoleOut = Console.Out;
            Console.SetOut(consoleOut);

            // Write the TestResult.xml
            if (testresultpath != null)
            {
                var testResultBuilder = new StringBuilder();
                using (var writer = new StringWriter(testResultBuilder))
                {
                    var xmlWriter = new XmlResultWriter(writer);
                    xmlWriter.SaveTestResult(result);
                }

                var xmlOutput = testResultBuilder.ToString();
                using (var writer = new StreamWriter(testresultpath))
                {
                    writer.Write(xmlOutput);
                }
            }
        }

        public void RunFinished(Exception exception)
        {
        }

        public void TestStarted(TestName testName)
        {
        }

        public void TestFinished(TestResult result)
        {
            // Add the test
            testResults.Add(result);

            // Append the info to the text output
            var output = consoleBuilder.ToString();
            if (String.IsNullOrWhiteSpace(output)) return;
            textOutputBuilder.AppendLine(result.Test.TestName.FullName).Append(output).AppendLine();
            consoleBuilder.Clear();

        }

        public void SuiteStarted(TestName testName)
        {
        }

        public void SuiteFinished(TestResult result)
        {
        }

        public void UnhandledException(Exception exception)
        {
        }

        public void TestOutput(TestOutput testOutput)
        {
        }

        #endregion EventListener
    }
}
