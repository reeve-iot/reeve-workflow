using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ReeveWorkflow.DependencyInjection;

/// <summary>One named place a host puts its implementation of a seam, filled where the library is configured
/// rather than somewhere else in the container.</summary>
/// <remarks>The point of a slot is that supplying a seam stops depending on the order two calls were written
/// in. A host that registers a seam separately wins or loses by position against the library's own answer; a
/// host that names it here beats that answer whichever way round the two lines sit, and the line that says so
/// sits next to the connection string it belongs with.
///
/// What a slot does not do is beat the host's own later registration: it is added where <c>AddWorkflows</c>
/// is called, and the last registration of a service type is the one that answers, so the same seam
/// registered again afterwards still wins. Two statements of one seam remain the thing to avoid.
///
/// A slot the host does not fill keeps the library's own answer, which is registered with <c>TryAdd</c> and so
/// still yields to a separate registration written earlier.</remarks>
/// <typeparam name="TService">The seam interface this slot fills.</typeparam>
public sealed class WorkflowSeam<TService> where TService : class
{
    private readonly ServiceDescriptor? fallback;
    private readonly ServiceLifetime defaultLifetime;
    private ServiceDescriptor? chosen;

    /// <summary>Creates a slot. Called by the package that owns the seam, not by a host — a host reaches
    /// slots that already exist through the options object.</summary>
    /// <param name="fallback">The library's own answer, or null for a seam it has none for.</param>
    public WorkflowSeam(ServiceDescriptor? fallback = null)
        : this(fallback, ServiceLifetime.Singleton)
    {
    }

    /// <summary>Creates a slot whose <see cref="Use{TImplementation}()"/> and <see cref="Use(Func{IServiceProvider, TService})"/>
    /// register with <paramref name="defaultLifetime"/> when the host names none.</summary>
    /// <param name="fallback">The library's own answer, or null for a seam it has none for.</param>
    /// <param name="defaultLifetime">Scoped for a seam read per call over a scope's own unit of work.</param>
    public WorkflowSeam(ServiceDescriptor? fallback, ServiceLifetime defaultLifetime)
    {
        this.fallback = fallback;
        this.defaultLifetime = defaultLifetime;
    }

    /// <summary>Whether the host filled this slot. A seam left at the library's answer reads as false.</summary>
    public bool IsHostSupplied => chosen is not null;

    /// <summary>The lifetime whatever fills this slot is registered with — the host's choice if it made one,
    /// otherwise the library's. A seam registered as a view of this one has to match it.</summary>
    public ServiceLifetime Lifetime => Descriptor?.Lifetime ?? ServiceLifetime.Singleton;

    private ServiceDescriptor? Descriptor => chosen ?? fallback;

    /// <summary>Fills the slot with a type the container constructs, with this slot's default lifetime: a
    /// singleton, unless the slot's own documentation says otherwise.</summary>
    public WorkflowSeam<TService> Use<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
        where TImplementation : class, TService =>
        Use<TImplementation>(defaultLifetime);

    /// <summary>Fills the slot with a type the container constructs, so the implementation takes its own
    /// dependencies through its constructor like any other service.</summary>
    /// <param name="lifetime">Scoped for an implementation that reads or writes through a request's own unit of
    /// work; singleton for one that holds nothing per call.</param>
    public WorkflowSeam<TService> Use<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(ServiceLifetime lifetime)
        where TImplementation : class, TService
    {
        chosen = ServiceDescriptor.Describe(typeof(TService), typeof(TImplementation), lifetime);
        return this;
    }

    /// <summary>Fills the slot with something built by hand, with this slot's default lifetime: a singleton,
    /// unless the slot's own documentation says otherwise.</summary>
    public WorkflowSeam<TService> Use(Func<IServiceProvider, TService> factory) => Use(factory, defaultLifetime);

    /// <summary>Fills the slot with something built by hand — for an implementation that wraps a service the
    /// host already has, or one whose absence it wants to degrade rather than fail on.</summary>
    public WorkflowSeam<TService> Use(Func<IServiceProvider, TService> factory, ServiceLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(factory);
        chosen = ServiceDescriptor.Describe(typeof(TService), factory, lifetime);
        return this;
    }

    /// <summary>Fills the slot with an object the host already holds. Always a singleton: the host made one and
    /// the container is being handed it, so there is no second for a scope to have.</summary>
    public WorkflowSeam<TService> UseInstance(TService instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        chosen = ServiceDescriptor.Singleton(typeof(TService), instance);
        return this;
    }

    /// <summary>Registers whatever this slot holds. A host's choice goes in outright; the library's own answer
    /// goes in with <c>TryAdd</c>, so a default can never displace something a host registered elsewhere.</summary>
    public void Register(IServiceCollection services)
    {
        if (Descriptor is not { } descriptor) return;

        if (IsHostSupplied) services.Add(descriptor);
        else services.TryAdd(descriptor);
    }
}
