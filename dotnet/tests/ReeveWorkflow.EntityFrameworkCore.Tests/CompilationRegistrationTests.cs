using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class CompilationRegistrationTests : IDisposable
{
    private const string StartOnly = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";

    private readonly SqliteConnection connection = new("Filename=:memory:");

    public CompilationRegistrationTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task A_store_in_Seams_Store_is_what_admission_reads()
    {
        var reads = new StoreReads();
        using var provider = Build(options => options.Seams.Store.Use(
            sp => new RecordingStore(new EfWorkflowStore(sp.GetRequiredService<WorkflowDbContext>()), reads),
            ServiceLifetime.Scoped));
        var workflowId = Seed(provider);

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        reads.Loaded.Should().Contain(workflowId);
    }

    [Fact]
    public async Task A_store_registered_in_the_container_before_AddWorkflows_is_what_admission_reads()
    {
        var reads = new StoreReads();
        using var provider = Build(before: services => services.AddScoped<IWorkflowStore>(
            sp => new RecordingStore(new EfWorkflowStore(sp.GetRequiredService<WorkflowDbContext>()), reads)));
        var workflowId = Seed(provider);

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        reads.Loaded.Should().Contain(workflowId);
    }

    [Fact]
    public async Task A_journal_in_Seams_RunJournal_is_what_a_run_is_opened_and_ended_through()
    {
        var calls = new JournalCalls();
        using var provider = Build(options => options.Seams.RunJournal.Use(
            sp => new RecordingJournal(new EfWorkflowRunJournal(sp.GetRequiredService<WorkflowDbContext>()), calls),
            ServiceLifetime.Scoped));
        var workflowId = Seed(provider);

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        calls.Opened.Should().Be(1);
        calls.Ended.Should().Be(1, "the run's ending and its steps are written through the journal that opened it");
    }

    [Fact]
    public async Task Under_the_default_registration_each_run_scope_gets_its_own_journal_over_its_own_context()
    {
        using var provider = Build();
        var workflowId = Seed(provider);
        var versionId = CurrentVersionOf(provider, workflowId);
        var scopes = provider.GetRequiredService<IWorkflowRunScopeFactory>();
        using var first = scopes.CreateScope();
        using var second = scopes.CreateScope();

        first.Journal.Should().BeOfType<EfWorkflowRunJournal>().And.NotBeSameAs(second.Journal);
        first.Store.Should().BeOfType<EfWorkflowStore>().And.NotBeSameAs(second.Store);
        var log = new WorkflowExecutionLog(
            Guid.NewGuid(), null, workflowId, versionId, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        await first.Journal.OpenRunAsync(log, default);

        first.Services.GetRequiredService<WorkflowDbContext>().ChangeTracker.Entries<WorkflowExecutionLog>()
            .Should().ContainSingle(e => e.Entity.Id == log.Id, "the journal writes on the context its scope resolves");
        second.Services.GetRequiredService<WorkflowDbContext>().ChangeTracker.Entries<WorkflowExecutionLog>()
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_store_and_the_journal_filled_without_a_lifetime_are_scoped_so_each_run_scope_has_its_own(bool factory)
    {
        using var provider = Build(options =>
        {
            if (factory)
            {
                options.Seams.Store.Use(sp => new EfWorkflowStore(sp.GetRequiredService<WorkflowDbContext>()));
                options.Seams.RunJournal.Use(sp => new EfWorkflowRunJournal(sp.GetRequiredService<WorkflowDbContext>()));
            }
            else
            {
                options.Seams.Store.Use<EfWorkflowStore>();
                options.Seams.RunJournal.Use<EfWorkflowRunJournal>();
            }
        });
        var scopes = provider.GetRequiredService<IWorkflowRunScopeFactory>();
        using var first = scopes.CreateScope();
        using var second = scopes.CreateScope();

        first.Store.Should().NotBeSameAs(second.Store);
        first.Journal.Should().NotBeSameAs(second.Journal);
    }

    [Fact]
    public void A_store_the_host_names_a_singleton_is_allowed_and_every_run_scope_reads_that_one_instance()
    {
        var store = new RecordingStore(null!, new StoreReads());
        using var provider = Build(options => options.Seams.Store.UseInstance(store));
        var scopes = provider.GetRequiredService<IWorkflowRunScopeFactory>();
        using var first = scopes.CreateScope();
        using var second = scopes.CreateScope();

        first.Store.Should().BeSameAs(store);
        second.Store.Should().BeSameAs(store);
    }

    [Fact]
    public void A_journal_slot_filled_with_a_singleton_is_refused_at_AddWorkflows()
    {
        var journal = new RecordingJournal(null, new JournalCalls());
        var building = () => Build(options => options.Seams.RunJournal.UseInstance(journal));

        building.Should().Throw<InvalidOperationException>().WithMessage("*singleton*Seams.RunJournal*registered in the container*");
    }

    [Fact]
    public void A_journal_slot_filled_with_a_factory_named_singleton_is_refused_at_AddWorkflows()
    {
        var building = () => Build(options => options.Seams.RunJournal.Use(
            _ => new RecordingJournal(null, new JournalCalls()), ServiceLifetime.Singleton));

        building.Should().Throw<InvalidOperationException>().WithMessage("*singleton*Seams.RunJournal*registered in the container*");
    }

    [Fact]
    public void A_journal_registered_in_the_container_as_a_singleton_before_AddWorkflows_is_refused()
    {
        var building = () => Build(before: services => services.AddSingleton<IWorkflowRunJournal>(
            _ => new RecordingJournal(null, new JournalCalls())));

        building.Should().Throw<InvalidOperationException>().WithMessage("*singleton*Seams.RunJournal*registered in the container*");
    }

    [Fact]
    public void The_refusal_reads_the_journal_registration_that_answers_which_is_the_last()
    {
        using var provider = Build(
            options => options.Seams.RunJournal.Use(
                sp => new RecordingJournal(new EfWorkflowRunJournal(sp.GetRequiredService<WorkflowDbContext>()), new JournalCalls()),
                ServiceLifetime.Scoped),
            before: services => services.AddSingleton<IWorkflowRunJournal>(_ => new RecordingJournal(null, new JournalCalls())));

        using var scope = provider.GetRequiredService<IWorkflowRunScopeFactory>().CreateScope();
        scope.Journal.Should().BeOfType<RecordingJournal>().Which.Should().NotBeSameAs(
            provider.CreateScope().ServiceProvider.GetRequiredService<IWorkflowRunJournal>());
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("factory")]
    [InlineData("replace")]
    public void A_singleton_journal_registered_after_AddWorkflows_is_refused_when_the_run_scope_factory_is_first_resolved(string how)
    {
        using var provider = Build(after: services =>
        {
            var journal = new RecordingJournal(null, new JournalCalls());
            switch (how)
            {
                case "instance": services.AddSingleton<IWorkflowRunJournal>(journal); break;
                case "factory": services.AddSingleton<IWorkflowRunJournal>(_ => journal); break;
                default: services.Replace(ServiceDescriptor.Singleton<IWorkflowRunJournal>(journal)); break;
            }
        });

        var resolving = () => provider.GetRequiredService<IWorkflowRunScopeFactory>();

        resolving.Should().Throw<InvalidOperationException>()
            .WithMessage("*singleton*Seams.RunJournal*registered in the container*after AddWorkflows*");
    }

    [Fact]
    public void A_singleton_journal_registered_after_AddWorkflows_is_refused_when_a_host_factory_opens_a_run_scope()
    {
        using var provider = Build(
            options => options.Seams.RunScopeFactory.Use(
                sp => new CountingScopes(new EfWorkflowRunScopeFactory(sp.GetRequiredService<IServiceScopeFactory>()), new ScopeCount())),
            after: services => services.AddSingleton<IWorkflowRunJournal>(new RecordingJournal(null, new JournalCalls())));
        var scopes = provider.GetRequiredService<IWorkflowRunScopeFactory>();

        var opening = () => scopes.CreateScope();

        opening.Should().Throw<InvalidOperationException>().WithMessage("*singleton*Seams.RunJournal*");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_keyed_singleton_or_a_transient_journal_is_not_refused_before_or_after_AddWorkflows(bool keyed)
    {
        Action<IServiceCollection> register = keyed
            ? services => services.AddKeyedSingleton<IWorkflowRunJournal>("audit", new RecordingJournal(null, new JournalCalls()))
            : services => services.AddTransient<IWorkflowRunJournal>(sp => new EfWorkflowRunJournal(sp.GetRequiredService<WorkflowDbContext>()));

        using var before = Build(before: register);
        using var after = Build(after: register);

        foreach (var provider in new[] { before, after })
        {
            using var first = provider.GetRequiredService<IWorkflowRunScopeFactory>().CreateScope();
            using var second = provider.GetRequiredService<IWorkflowRunScopeFactory>().CreateScope();
            first.Journal.Should().BeAssignableTo<EfWorkflowRunJournal>().And.NotBeSameAs(second.Journal);
        }
    }

    [Fact]
    public void The_default_journal_builds_over_a_provider_that_answers_null_for_contributors()
    {
        var services = new ServiceCollection();
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(context => context.UseSqlite(connection));
        });
        var factory = services.Single(d => d.ServiceType == typeof(IWorkflowRunJournal)).ImplementationFactory!;
        using var db = new WorkflowDbContext(new DbContextOptionsBuilder<WorkflowDbContext>().UseSqlite(connection).Options);

        factory(new OnlyTheContext(db)).Should().BeOfType<EfWorkflowRunJournal>();
    }

    [Fact]
    public void EfWorkflowStore_declares_every_member_of_IWorkflowStore_so_no_default_body_is_inherited()
    {
        var map = typeof(EfWorkflowStore).GetInterfaceMap(typeof(IWorkflowStore));

        map.TargetMethods.Should().OnlyContain(m => m.DeclaringType == typeof(EfWorkflowStore));
        map.TargetMethods.Should().HaveCount(4);
    }

    [Fact]
    public void A_host_that_fills_nothing_gets_a_compiler_with_the_library_defaults()
    {
        using var provider = Build();

        provider.GetRequiredService<WorkflowCompilationService>().Should().BeSameAs(
            provider.GetRequiredService<WorkflowCompilationService>(), "one service, so its memo is worth having");
        provider.GetRequiredService<IWorkflowCompilationStore>().Should().BeOfType<EfWorkflowCompilationStore>();
        var options = provider.GetRequiredService<CompilationOptions>();
        options.Vocabulary.Table.Should().BeSameAs(PortTypeWideningTable.Builtin);
        options.NamespaceRegistrations.Should().BeEmpty();
    }

    [Fact]
    public void A_host_record_is_added_to_the_builtins_rather_than_replacing_them()
    {
        using var provider = Build(options => options.Seams.PortTypes
            .Add(new WorkflowTypeRecord("BondedDepot", typeof(string)) { WidensTo = ["Depot"] })
            .Add(new WorkflowTypeRecord("Depot", typeof(string)) { Aliases = ["Warehouse"] })
            .Add(new WorkflowTypeRecord("Manifest", typeof(string))));

        var vocabulary = provider.GetRequiredService<CompilationOptions>().Vocabulary;

        vocabulary.IsAssignable(PortValueType.Of("BondedDepot"), PortValueType.Of("Depot")).Should().BeTrue();
        vocabulary.Table.Canonicalize(PortValueType.Of("Warehouse")).Should().Be(PortValueType.Of("Depot"));
        vocabulary.Table.KnownTypes.Should().Contain(PortValueType.Of("Manifest"));
        vocabulary.IsAssignable(PortValueType.Int, PortValueType.Number).Should().BeTrue(
            "a host adds to the vocabulary; it does not hand in a replacement");
        provider.GetRequiredService<IWorkflowTypeRegistry>().Current.Should().BeSameAs(vocabulary);
        vocabulary.Generation.Should().Be(0, "a host registers at startup, generation 0");
    }

    [Fact]
    public async Task A_host_row_reaches_what_the_registered_compiler_compiles()
    {
        var blocks = (Action<IServiceCollection>)(services =>
        {
            services.AddSingleton<IWorkflowBlockFactory>(new PortsFactory("Host.Bonded", outputs: [new("Out", "Out", PortValueType.Of("BondedDepot"), false)]));
            services.AddSingleton<IWorkflowBlockFactory>(new PortsFactory("Host.Deliver", inputs: [new("In", "In", PortValueType.Of("Depot"), true)]));
        });
        using var widened = Build(
            options => options.Seams.PortTypes
                .Add(new WorkflowTypeRecord("Depot", typeof(string)))
                .Add(new WorkflowTypeRecord("BondedDepot", typeof(string)) { WidensTo = ["Depot"] }),
            before: blocks);
        using var plain = Build(
            options => options.Seams.PortTypes
                .Add(new WorkflowTypeRecord("Depot", typeof(string)))
                .Add(new WorkflowTypeRecord("BondedDepot", typeof(string))),
            before: blocks);

        var valid = await widened.GetRequiredService<WorkflowCompilationService>().EnsureAsync(Version(BondedToDepot), null, default);
        var mismatched = await plain.GetRequiredService<WorkflowCompilationService>().EnsureAsync(Version(BondedToDepot), null, default);

        valid.Diagnostics.Should().BeEmpty();
        mismatched.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    [Fact]
    public async Task A_plugin_registering_a_type_changes_the_next_compile_of_a_version_already_compiled()
    {
        var blocks = (Action<IServiceCollection>)(services =>
        {
            services.AddSingleton<IWorkflowBlockFactory>(new PortsFactory("Host.Bonded", outputs: [new("Out", "Out", PortValueType.Of("Pallet"), false)]));
            services.AddSingleton<IWorkflowBlockFactory>(new PortsFactory("Host.Deliver", inputs: [new("In", "In", PortValueType.Of("Depot"), true)]));
        });
        using var provider = Build(options => options.Seams.PortTypes.Add(new WorkflowTypeRecord("Depot", typeof(string))), before: blocks);
        var service = provider.GetRequiredService<WorkflowCompilationService>();
        var registry = provider.GetRequiredService<IWorkflowTypeRegistry>();
        var version = Version(BondedToDepot);

        var before = await service.EnsureAsync(version, null, default);
        registry.Register("plugin:pallets", [new WorkflowTypeRecord("Pallet", typeof(string)) { WidensTo = ["Depot"] }]).Accepted.Should().BeTrue();
        var loaded = await service.EnsureAsync(version, null, default);
        registry.Unregister("plugin:pallets").Should().BeTrue();
        var unloaded = await service.EnsureAsync(version, null, default);

        before.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.PortTypeUnregistered);
        loaded.Diagnostics.Should().BeEmpty("the memo and the stored verdict follow the registry generation");
        unloaded.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.PortTypeUnregistered);
    }

    [Fact]
    public void A_host_constructor_is_registered_beside_the_builtins_and_a_clashing_name_is_refused_at_AddWorkflows()
    {
        var gauge = new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = ["Boolean", "Number"] };
        using var provider = Build(options => options.Seams.PortTypes
            .Add(new WorkflowTypeRecord("Manifest", typeof(string)))
            .Add(gauge));
        var clashing = () => Build(options => options.Seams.PortTypes
            .Add(new WorkflowTypeRecord("Gauge", typeof(string)))
            .Add(gauge));

        var vocabulary = provider.GetRequiredService<CompilationOptions>().Vocabulary;

        vocabulary.Table.Constructors.Should().ContainSingle().Which.Should().BeSameAs(gauge);
        vocabulary.RefusalOf(PortTerm.Parse("Gauge<Number>", PortTermGrammar.Document)).Should().BeNull();
        vocabulary.RefusalOf(PortTerm.Parse("Gauge<Manifest>", PortTermGrammar.Document))!.Code.Should().Be("port-type-unregistered");
        clashing.Should().Throw<WorkflowTypeRegistrationException>()
            .Which.Refusals.Should().ContainSingle(r => r.Code == "port-type-name-clash" && r.Record == "Gauge");
    }

    [Fact]
    public void Two_records_claiming_one_name_or_alias_are_refused_at_AddWorkflows_whatever_their_order()
    {
        var aliasTwice = () => Build(options => options.Seams.PortTypes
            .Add(new WorkflowTypeRecord("Depot", typeof(string)) { Aliases = ["Yard"] })
            .Add(new WorkflowTypeRecord("Warehouse", typeof(string)) { Aliases = ["yard"] }));
        var aliasIsAName = () => Build(options => options.Seams.PortTypes
            .Add(new WorkflowTypeRecord("Yard", typeof(string)))
            .Add(new WorkflowTypeRecord("Depot", typeof(string)) { Aliases = ["Yard"] }));

        aliasTwice.Should().Throw<WorkflowTypeRegistrationException>()
            .Which.Refusals.Should().ContainSingle(r => r.Code == "port-type-name-clash" && r.Record == "Warehouse");
        aliasIsAName.Should().Throw<WorkflowTypeRegistrationException>()
            .Which.Refusals.Should().ContainSingle(r => r.Code == "port-type-name-clash" && r.Record == "Depot");
    }

    [Fact]
    public void A_record_colliding_with_a_builtin_name_or_alias_is_refused_at_AddWorkflows()
    {
        var alias = () => Build(options => options.Seams.PortTypes.Add(new WorkflowTypeRecord("Flag", typeof(bool)) { Aliases = ["bool"] }));
        var name = () => Build(options => options.Seams.PortTypes.Add(new WorkflowTypeRecord("json", typeof(string))));

        alias.Should().Throw<WorkflowTypeRegistrationException>().Which.Refusals.Should().ContainSingle(r => r.Code == "port-type-name-clash");
        name.Should().Throw<WorkflowTypeRegistrationException>().Which.Refusals.Should().ContainSingle(r => r.Code == "port-type-name-clash");
    }

    [Fact]
    public void A_collection_named_record_or_a_row_to_an_unregistered_term_is_refused_at_AddWorkflows()
    {
        var row = () => Build(options => options.Seams.PortTypes
            .Add(new WorkflowTypeRecord("Route", typeof(string)) { WidensTo = ["Collection<Stop>"] }));
        var named = () => Build(options => options.Seams.PortTypes.Add(new WorkflowTypeRecord("Collection<Route>", typeof(string))));

        row.Should().Throw<WorkflowTypeRegistrationException>()
            .Which.Refusals.Should().ContainSingle(r => r.Code == "port-type-registration-invalid" && r.Message.Contains("Stop"));
        named.Should().Throw<WorkflowTypeRegistrationException>()
            .Which.Refusals.Should().ContainSingle(r => r.Code == "port-type-registration-invalid" && r.Record == "Collection<Route>");
    }

    [Fact]
    public async Task A_host_namespace_registration_reaches_compilation_and_two_for_one_namespace_are_refused_at_AddWorkflows()
    {
        var notify = (Action<IServiceCollection>)(services =>
        {
            services.AddSingleton<IWorkflowBlockFactory>(
                new PortsFactory("Host.Notify", inputs: [new("Message", "Message", PortValueType.String, true)]));
        });
        using var provider = Build(options => options.Seams.ExpressionNamespaces.Add(HostNamespaceRegistration.For(new DepotCheck("depot", NamespacePathCheck.No))), before: notify);
        using var withoutACheck = Build(before: notify);
        var version = Version("""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n","blockTypeId":"Host.Notify","configuration":{"Message":"expr:${depot.missing}"},"position":{"x":0,"y":0}}],"edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"n","mappings":{}}]}""");

        var refused = await provider.GetRequiredService<WorkflowCompilationService>().EnsureAsync(version, null, default);
        var left = await withoutACheck.GetRequiredService<WorkflowCompilationService>().EnsureAsync(version, null, default);

        refused.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionUnknownNamespace);
        left.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionUnknownNamespace);

        var twice = () => Build(options =>
        {
            options.Seams.ExpressionNamespaces.Add(HostNamespaceRegistration.For(new DepotCheck("depot")));
            options.Seams.ExpressionNamespaces.Add(HostNamespaceRegistration.For(new DepotCheck("depot")));
        });
        twice.Should().Throw<ArgumentException>().WithMessage("*depot*");
    }

    [Fact]
    public async Task A_store_in_Seams_Store_is_what_the_compiler_reads_a_called_workflow_from()
    {
        var reads = new StoreReads();
        using var provider = Build(
            options => options.Seams.Store.Use(
                sp => new RecordingStore(new EfWorkflowStore(sp.GetRequiredService<WorkflowDbContext>()), reads),
                ServiceLifetime.Scoped),
            before: services => services.AddSingleton<IWorkflowBlockFactory>(new SubWorkflowFactory()));
        var child = Guid.NewGuid();

        await provider.GetRequiredService<WorkflowCompilationService>().EnsureAsync(CallerOf(child), null, default);

        reads.Loaded.Should().Equal(child);
    }

    [Fact]
    public async Task A_run_scope_factory_in_its_slot_is_what_both_the_compiler_and_the_engine_open()
    {
        var opened = new ScopeCount();
        using var provider = Build(options => options.Seams.RunScopeFactory.Use(
            sp => new CountingScopes(new EfWorkflowRunScopeFactory(sp.GetRequiredService<IServiceScopeFactory>()), opened)));
        var workflowId = Seed(provider);
        var service = provider.GetRequiredService<WorkflowCompilationService>();
        var afterConstruction = opened.Count;

        await service.EnsureAsync(CallerOf(Guid.NewGuid()), null, default);
        var afterCompile = opened.Count;
        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        afterConstruction.Should().Be(1, "the constructor checks the resolvers in one scope");
        afterCompile.Should().Be(2);
        opened.Count.Should().BeGreaterThan(2, "the run opened its scope from the same factory");
    }

    [Fact]
    public async Task A_memo_hit_opens_no_run_scope_unless_its_definition_has_a_node_resolved_elsewhere()
    {
        var opened = new ScopeCount();
        using var provider = Build(
            options =>
            {
                options.Seams.CompilationStore.UseInstance(new KeptVerdicts());
                options.Seams.RunScopeFactory.Use(sp => new CountingScopes(
                    new EfWorkflowRunScopeFactory(sp.GetRequiredService<IServiceScopeFactory>()), opened));
            },
            before: services => services.AddSingleton<IWorkflowBlockFactory>(new SubWorkflowFactory()));
        var service = provider.GetRequiredService<WorkflowCompilationService>();
        var plain = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, StartOnly, null, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var caller = CallerOf(Guid.NewGuid());
        await service.EnsureAsync(plain, null, default);
        await service.EnsureAsync(caller, null, default);
        var afterTheMisses = opened.Count;

        for (var i = 0; i < 10; i++)
            await service.EnsureAsync(plain, null, default);
        var afterTenPlainHits = opened.Count;
        await service.EnsureAsync(caller, null, default);

        afterTenPlainHits.Should().Be(afterTheMisses);
        opened.Count.Should().Be(afterTheMisses + 1);
    }

    [Fact]
    public async Task A_scoped_node_port_resolver_registered_in_the_container_is_asked_from_the_call_scope()
    {
        var asked = new ScopeCount();
        using var provider = Build(before: services =>
        {
            services.AddSingleton<IWorkflowBlockFactory>(new HostCallFactory());
            services.AddScoped<INodePortResolver>(_ => new CountingResolver(asked));
        });
        var version = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1,
            """{"nodes":[{"id":"call","blockTypeId":"Host.Call","configuration":{"Callee":"x"},"position":{"x":0,"y":0}}],"edges":[]}""",
            null, null, Guid.NewGuid(), DateTimeOffset.UtcNow);

        await provider.GetRequiredService<WorkflowCompilationService>().EnsureAsync(version, null, default);

        asked.Count.Should().Be(1);
    }

    [Fact]
    public void Two_container_resolvers_for_one_block_type_refuse_when_the_service_is_first_built()
    {
        using var provider = Build(before: services =>
        {
            services.AddSingleton<INodePortResolver>(new CountingResolver(new ScopeCount()));
            services.AddSingleton<INodePortResolver>(new CountingResolver(new ScopeCount()));
        });

        var resolving = () => provider.GetRequiredService<WorkflowCompilationService>();

        resolving.Should().Throw<ArgumentException>().WithMessage("*Host.Call*");
    }

    [Fact]
    public async Task A_verdict_is_stamped_by_the_registered_TimeProvider()
    {
        var clock = new SettableClock(new DateTimeOffset(2031, 1, 2, 3, 4, 5, TimeSpan.Zero));
        using var provider = Build(before: services => services.AddSingleton<TimeProvider>(clock));

        var verdict = await provider.GetRequiredService<WorkflowCompilationService>()
            .EnsureAsync(CallerOf(Guid.NewGuid()), null, default);

        verdict.CompiledAtUtc.Should().Be(clock.Now);
    }

    private ServiceProvider Build(
        Action<WorkflowPersistenceOptions>? configure = null, Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        before?.Invoke(services);
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(context => context.UseSqlite(connection));
            configure?.Invoke(options);
        }).AddWorkflowEngine();
        after?.Invoke(services);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
        return provider;
    }

    private static Guid Seed(IServiceProvider provider)
    {
        var workflowId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = new Workflow(workflowId, "wf", null, ownerId, now);
        db.Workflows.Add(workflow);
        db.SaveChanges();
        var version = new WorkflowVersion(Guid.NewGuid(), workflowId, 1, StartOnly, null, null, ownerId, now, isPublished: true);
        db.WorkflowVersions.Add(version);
        db.SaveChanges();
        workflow.SetCurrentVersion(version, now);
        db.SaveChanges();
        return workflowId;
    }

    private static Guid CurrentVersionOf(IServiceProvider provider, Guid workflowId)
    {
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Workflows
            .Single(w => w.Id == workflowId).CurrentVersionId!.Value;
    }

    private static WorkflowVersion CallerOf(Guid child) => new(Guid.NewGuid(), Guid.NewGuid(), 1,
        $$$"""{"nodes":[{"id":"call","blockTypeId":"SubWorkflow","configuration":{"WorkflowId":"{{{child}}}"},"position":{"x":0,"y":0}}],"edges":[]}""",
        null, null, Guid.NewGuid(), DateTimeOffset.UtcNow);

    private const string BondedToDepot = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"a","blockTypeId":"Host.Bonded","position":{"x":0,"y":0},"configuration":{}},{"id":"b","blockTypeId":"Host.Deliver","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"a","mappings":{}},{"id":"e2","sourceNodeId":"a","targetNodeId":"b","mappings":{"In":{"type":"direct","sourceOutputName":"Out"}}}]}""";

    private static WorkflowVersion Version(string definition) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, definition, null, null, Guid.NewGuid(), DateTimeOffset.UtcNow);

    private sealed class DepotCheck(string ns, NamespacePathCheck answer = NamespacePathCheck.Yes) : IExpressionNamespaceStaticCheck
    {
        public string Namespace => ns;

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(answer, "String");
    }

    private sealed class PortsFactory(
        string blockTypeId, BlockPortDefinition[]? inputs = null, BlockPortDefinition[]? outputs = null) : IWorkflowBlockFactory
    {
        public string BlockTypeId => blockTypeId;
        public string DisplayName => blockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs ?? [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs ?? [];

        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private sealed class ScopeCount
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public void Add() => Interlocked.Increment(ref count);
    }

    private sealed class CountingScopes(IWorkflowRunScopeFactory inner, ScopeCount opened) : IWorkflowRunScopeFactory
    {
        public IWorkflowRunScope CreateScope()
        {
            opened.Add();
            return inner.CreateScope();
        }
    }

    private sealed class CountingResolver(ScopeCount asked) : INodePortResolver
    {
        public string BlockTypeId => "Host.Call";

        public Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken)
        {
            asked.Add();
            return Task.FromResult(NodePortResolution.Failed("a test resolver"));
        }
    }

    private class RunsAWorkflowFactory(string blockTypeId, string referenceInput) : IWorkflowBlockFactory
    {
        public string BlockTypeId => blockTypeId;
        public string DisplayName => blockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [new BlockPortDefinition(referenceInput, referenceInput, PortValueType.String, true)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool PortsAreResolvedElsewhere => true;
        public string? RunsWorkflowFrom => referenceInput;

        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private sealed class SubWorkflowFactory() : RunsAWorkflowFactory("SubWorkflow", "WorkflowId");

    private sealed class HostCallFactory() : RunsAWorkflowFactory("Host.Call", "Callee");

    private sealed class KeptVerdicts : IWorkflowCompilationStore
    {
        public Task<CompilationVerdict?> FindAsync(Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken) =>
            Task.FromResult<CompilationVerdict?>(null);

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> DeleteStaleAsync(IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class OnlyTheContext(WorkflowDbContext db) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(WorkflowDbContext) ? db : null;
    }

    private sealed class StoreReads
    {
        public List<Guid> Loaded { get; } = [];
    }

    private sealed class RecordingStore(IWorkflowStore inner, StoreReads reads) : IWorkflowStore
    {
        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            lock (reads) reads.Loaded.Add(workflowId);
            return inner.LoadAsync(workflowId, cancellationToken);
        }

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
            inner.LoadVersionAsync(versionId, cancellationToken);

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(Guid workflowId, CancellationToken cancellationToken) =>
            inner.LoadCurrentVersionOfAsync(workflowId, cancellationToken);

        public Task<WorkflowVersion?> LoadVersionNumberedAsync(Guid workflowId, int versionNumber, CancellationToken cancellationToken) =>
            inner.LoadVersionNumberedAsync(workflowId, versionNumber, cancellationToken);
    }

    private sealed class JournalCalls
    {
        public int Opened;
        public int Saved;
        public int Ended;
    }

    private sealed class RecordingJournal(IWorkflowRunJournal? inner, JournalCalls calls) : IWorkflowRunJournal
    {
        public Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls.Opened);
            return inner!.OpenRunAsync(log, cancellationToken);
        }

        public Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            inner!.LoadRunAsync(executionLogId, cancellationToken);

        public Task SaveRunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls.Saved);
            return inner!.SaveRunAsync(cancellationToken);
        }

        public Task SaveRunDetailsAsync(
            Guid? executionId, bool recordLogEntries, IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
            IReadOnlyList<WorkflowExecutionStep> executionSteps, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls.Ended);
            return inner!.SaveRunDetailsAsync(executionId, recordLogEntries, logEntries, executionSteps, cancellationToken);
        }
    }
}
