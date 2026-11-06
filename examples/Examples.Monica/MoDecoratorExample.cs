using Microsoft.Extensions.DependencyInjection;
using Monica.Experimental.Decorator;

namespace Examples.Monica;

/// <summary>
/// MoDecorator usage example
/// Shows how to use InterfaceProxyDecorationStrategy to decorate all services that implement a specific interface
/// </summary>
public static class MoDecoratorExample
{
    /// <summary>
    /// Basic warehousing interface
    /// </summary>
    public interface IRepository
    {
        Task<string> GetDataAsync(int id);
    }

    /// <summary>
    /// User warehousing interface
    /// </summary>
    public interface IUserRepository : IRepository
    {
        Task<string> GetUserNameAsync(int userId);
    }

    /// <summary>
    /// Product warehousing interface
    /// </summary>
    public interface IProductRepository : IRepository
    {
        Task<string> GetProductNameAsync(int productId);
    }

    /// <summary>
    /// Generic processor interface
    /// </summary>
    /// <typeparam name="T">Data types processed</typeparam>
    public interface IHandler<T>
    {
        Task<T> HandleAsync(T data);
    }

    /// <summary>
    /// User warehousing implementation
    /// </summary>
    public class UserRepository : IUserRepository
    {
        public Task<string> GetDataAsync(int id) => Task.FromResult($"User data: {id}");
        public Task<string> GetUserNameAsync(int userId) => Task.FromResult($"User_{userId}");
    }

    /// <summary>
    /// Product warehousing implementation
    /// </summary>
    public class ProductRepository : IProductRepository
    {
        public Task<string> GetDataAsync(int id) => Task.FromResult($"Product data: {id}");
        public Task<string> GetProductNameAsync(int productId) => Task.FromResult($"Product_{productId}");
    }

    /// <summary>
    /// String processor implementation
    /// </summary>
    public class StringHandler : IHandler<string>
    {
        public Task<string> HandleAsync(string data) => Task.FromResult($"Handled: {data}");
    }

    /// <summary>
    /// Integer processor implementation
    /// </summary>
    public class IntHandler : IHandler<int>
    {
        public Task<int> HandleAsync(int data) => Task.FromResult(data * 2);
    }

    /// <summary>
    /// Repository cache decorator
    /// </summary>
    public class CachingRepositoryDecorator : IRepository
    {
        private readonly IRepository _inner;

        public CachingRepositoryDecorator(IRepository inner)
        {
            _inner = inner;
        }

        public async Task<string> GetDataAsync(int id)
        {
            // Simple caching logic simulation
            Console.WriteLine($"[Cache] Checking cache for repository data: {id}");
            var result = await _inner.GetDataAsync(id);
            Console.WriteLine($"[Cache] Cached repository data: {id} -> {result}");
            return result;
        }
    }

    /// <summary>
    /// Generic log decorator
    /// </summary>
    /// <typeparam name="T">Data types processed</typeparam>
    public class LoggingHandler<T> : IHandler<T>
    {
        private readonly IHandler<T> _inner;

        public LoggingHandler(IHandler<T> inner)
        {
            _inner = inner;
        }

        public async Task<T> HandleAsync(T data)
        {
            Console.WriteLine($"[Log] Handling {typeof(T).Name}: {data}");
            var result = await _inner.HandleAsync(data);
            Console.WriteLine($"[Log] Handled {typeof(T).Name}: {data} -> {result}");
            return result;
        }
    }

    /// <summary>
    /// Demonstrate the use of InterfaceProxyDecorationStrategy
    /// </summary>
    public static async Task RunExample()
    {
        var services = new ServiceCollection();

        // Registration service
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IHandler<string>, StringHandler>();
        services.AddScoped<IHandler<int>, IntHandler>();

        // Use InterfaceProxyDecorationStrategy to decorate all services that implement IRepository
        services.DecorateInterfaceProxy<IRepository, CachingRepositoryDecorator>();

        // Decorate all services that implement IHandler<T> with delegates
        services.DecorateInterfaceProxy(typeof(IHandler<>), typeof(LoggingHandler<>));

        // You can also use functional decoration
        services.DecorateInterfaceProxy<IRepository>(repo => 
        {
            return new FunctionalRepositoryDecorator(repo);
        });

        var serviceProvider = services.BuildServiceProvider();

        Console.WriteLine("=== MoDecorator InterfaceProxy Example ===\n");

        // Test warehouse decoration
        Console.WriteLine("1. 测试仓储装饰:");
        var userRepo = serviceProvider.GetRequiredService<IUserRepository>();
        var userData = await userRepo.GetDataAsync(123);
        Console.WriteLine($"Result: {userData}\n");

        var productRepo = serviceProvider.GetRequiredService<IProductRepository>();
        var productData = await productRepo.GetDataAsync(456);
        Console.WriteLine($"Result: {productData}\n");

        // Testing generic handler decorations
        Console.WriteLine("2. 测试泛型处理器装饰:");
        var stringHandler = serviceProvider.GetRequiredService<IHandler<string>>();
        var stringResult = await stringHandler.HandleAsync("Hello World");
        Console.WriteLine($"String Result: {stringResult}\n");

        var intHandler = serviceProvider.GetRequiredService<IHandler<int>>();
        var intResult = await intHandler.HandleAsync(42);
        Console.WriteLine($"Int Result: {intResult}\n");

        Console.WriteLine("=== Example Completed ===");
    }

    /// <summary>
    /// Functional repository decorator example
    /// </summary>
    private class FunctionalRepositoryDecorator : IRepository
    {
        private readonly IRepository _inner;

        public FunctionalRepositoryDecorator(IRepository inner)
        {
            _inner = inner;
        }

        public async Task<string> GetDataAsync(int id)
        {
            Console.WriteLine($"[Functional] Processing repository request: {id}");
            var result = await _inner.GetDataAsync(id);
            Console.WriteLine($"[Functional] Processed repository request: {id}");
            return result;
        }
    }
}
