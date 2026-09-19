namespace GameLauncher.Tests.TestSupport;

/// <summary>
/// Every test class that needs a real WPF Application/Dispatcher shares this ONE collection so xUnit
/// creates exactly one WpfStaFixture for the whole run and runs those classes sequentially against it -
/// System.Windows.Application enforces "at most one instance per process," and xUnit runs different test
/// CLASSES in parallel by default (an IClassFixture per class does NOT prevent that - it only means each
/// class gets its own fixture INSTANCE, which is exactly what caused two classes' fixtures to race to
/// construct competing Application objects on separate STA threads and crash the whole test host).
/// Add [Collection(WpfStaCollection.Name)] to any test class that takes a WpfStaFixture constructor
/// parameter.
/// </summary>
[CollectionDefinition(Name)]
public class WpfStaCollection : ICollectionFixture<WpfStaFixture>
{
    public const string Name = "WPF STA";
}
