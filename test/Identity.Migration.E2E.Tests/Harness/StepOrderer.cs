using Xunit.Abstractions;
using Xunit.Sdk;

namespace Identity.Migration.E2E.Tests.Harness;

/// <summary>Saga phases are stateful and must run in order; each [Step(n)] is a separately reported test.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StepAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

public sealed class StepOrderer : ITestCaseOrderer
{
    public const string TypeName = "Identity.Migration.E2E.Tests.Harness.StepOrderer";
    public const string AssemblyName = "Identity.Migration.E2E.Tests";

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
        testCases.OrderBy(tc => tc.TestMethod.Method
            .GetCustomAttributes(typeof(StepAttribute).AssemblyQualifiedName!)
            .Select(a => a.GetNamedArgument<int>(nameof(StepAttribute.Order)))
            .DefaultIfEmpty(int.MaxValue)
            .First());
}
