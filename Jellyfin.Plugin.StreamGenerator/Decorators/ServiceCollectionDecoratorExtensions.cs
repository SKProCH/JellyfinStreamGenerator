using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.StreamGenerator.Decorators;

public static class ServiceCollectionDecoratorExtensions {
    private const string DecoratedServiceKeySuffix = "+Decorated";

    public static IServiceCollection Decorate<TService, TDecorator>(this IServiceCollection services)
        where TDecorator : TService {
        return services.Decorate(typeof(TService), typeof(TDecorator));
    }

    public static IServiceCollection Decorate<TService, TDecorator, TAlias>(this IServiceCollection services)
        where TDecorator : TService, TAlias {
        return Decorate(services, typeof(TService), typeof(TDecorator), typeof(TAlias));
    }

    public static IServiceCollection Decorate(this IServiceCollection services, Type serviceType, Type decoratorType) {
        return Decorate(services, serviceType, decoratorType, null);
    }

    private static IServiceCollection Decorate(IServiceCollection services, Type serviceType, Type decoratorType,
        Type? aliasType) {
        var decorated = false;
        var aliasDescriptor = aliasType is null
            ? null
            : services.LastOrDefault(descriptor => descriptor.ServiceType == serviceType && !IsDecorated(descriptor));

        var isOpenGeneric = serviceType.IsGenericTypeDefinition;

        for (var i = services.Count - 1; i >= 0; i--) {
            var descriptor = services[i];

            if (IsDecorated(descriptor))
                continue;

            var canDecorate = isOpenGeneric
                ? descriptor.ServiceType.IsGenericType
                  && !descriptor.ServiceType.IsGenericTypeDefinition
                  && descriptor.ServiceType.GetGenericTypeDefinition() == serviceType
                : descriptor.ServiceType == serviceType;

            if (!canDecorate)
                continue;

            var closedDecoratorType = isOpenGeneric
                ? decoratorType.MakeGenericType(descriptor.ServiceType.GetGenericArguments())
                : decoratorType;

            var serviceKey = $"{descriptor.ServiceType.Name}+{Guid.NewGuid():n}{DecoratedServiceKeySuffix}";

            services.Add(CreateKeyedDescriptor(descriptor, serviceKey));
            var factory = CreateDecoratorFactory(descriptor.ServiceType, serviceKey, closedDecoratorType);
            if (aliasType is null || !ReferenceEquals(descriptor, aliasDescriptor)) {
                services[i] = new ServiceDescriptor(descriptor.ServiceType, factory, descriptor.Lifetime);
            }
            else {
                var decoratorKey = $"{closedDecoratorType.Name}+{Guid.NewGuid():n}{DecoratedServiceKeySuffix}";
                services.Add(new ServiceDescriptor(closedDecoratorType, decoratorKey,
                    (provider, _) => factory(provider), descriptor.Lifetime));

                object ResolveDecorator(IServiceProvider provider) {
                    return provider.GetRequiredKeyedService(closedDecoratorType, decoratorKey);
                }

                services[i] = new ServiceDescriptor(descriptor.ServiceType, ResolveDecorator, descriptor.Lifetime);
                services.Add(new ServiceDescriptor(aliasType, ResolveDecorator, descriptor.Lifetime));
            }

            decorated = true;
        }

        if (!decorated) {
            throw new InvalidOperationException($"No service of type {serviceType.Name} has been registered.");
        }

        return services;
    }

    private static ServiceDescriptor CreateKeyedDescriptor(ServiceDescriptor original, object serviceKey) {
        if (original.ImplementationInstance != null)
            return new ServiceDescriptor(original.ServiceType, serviceKey, original.ImplementationInstance);

        if (original.ImplementationFactory != null)
            return new ServiceDescriptor(original.ServiceType, serviceKey,
                (sp, _) => original.ImplementationFactory(sp), original.Lifetime);

        return new ServiceDescriptor(original.ServiceType, serviceKey, original.ImplementationType!, original.Lifetime);
    }

    private static Func<IServiceProvider, object> CreateDecoratorFactory(Type serviceType, object serviceKey,
        Type decoratorType) {
        return sp => {
            var inner = ((IKeyedServiceProvider)sp).GetRequiredKeyedService(serviceType, serviceKey);
            return ActivatorUtilities.CreateInstance(sp, decoratorType, inner);
        };
    }

    private static bool IsDecorated(ServiceDescriptor descriptor) {
        return descriptor.ServiceKey is string key && key.EndsWith(DecoratedServiceKeySuffix);
    }
}