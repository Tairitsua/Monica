# MoChainTracing 调用链追踪模块

MoChainTracing 是一个基于 `AsyncLocal` 技术的应用层调用链追踪模块，用于记录接口调用过程中的各种行为，包括领域服务调用、数据库调用、Redis 调用、外部 API 调用等，并将调用链信息附加到 `IResultEnvelope` 的 `Metadata` 中。

## 主要特性

- 🚀 基于 `AsyncLocal` 技术，支持异步调用链追踪
- 🎯 自动追踪控制器 Action 执行
- 📊 支持数据库、Redis、外部 API 等常见调用追踪
- 🔧 灵活的配置选项和扩展方式
- 📝 详细的调用链信息和执行时间统计
- 🛡️ 异常处理和错误追踪
- 🎨 简洁的 API 和扩展方法

## 快速开始

### 1. 注册服务

在 `Program.cs` 或 `Startup.cs` 中注册调用链追踪服务：

```csharp
// 基本注册
builder.Services.AddMoChainTracing();

// 或者使用 MVC 集成（自动添加 ActionFilter）
builder.Services.AddMoChainTracingMvc();

// 禁用调用链追踪（使用空实现）
builder.Services.DisableChainTracing();
// 或者
builder.Services.DisableMoChainTracing();

// 条件性启用调用链追踪
builder.Services.AddChainTracing(enabled: !builder.Environment.IsProduction());

// 或者使用条件函数
builder.Services.AddChainTracing(() => builder.Configuration.GetValue<bool>("Features:EnableChainTracing"));

// 或者自定义配置
builder.Services.AddMoChainTracing(options =>
{
    options.EnableAutoTracing = true;
    options.EnableMiddleware = true;
    options.EnableActionFilter = true;
    options.MaxChainDepth = 50;
    options.LogRequestParameters = true;
});

// 通过配置禁用
builder.Services.AddMoChainTracing(options =>
{
    options.EnableAutoTracing = false; // 这将自动注册空实现
});
```

### 2. 注册中间件

在应用程序构建中注册中间件：

```csharp
var app = builder.Build();

// 使用调用链追踪中间件
app.UseMoChainTracing();

// 或者带配置
app.UseMoChainTracing(options =>
{
    options.EnableMiddleware = true;
});
```

## 使用方式

### 1. 手动调用链追踪

```csharp
public class UserService
{
    private readonly IMoChainTracing _chainTracing;

    public UserService(IMoChainTracing chainTracing)
    {
        _chainTracing = chainTracing;
    }

    public async Task<ServiceResponse<User>> GetUserAsync(int userId)
    {
        // 方式一：手动开始和结束
        var traceId = _chainTracing.BeginTrace("UserService", "GetUser", new { UserId = userId });
        try
        {
            var user = await GetUserFromDatabase(userId);
            _chainTracing.EndTrace(traceId, "Success", true);
            return ServiceResponse.Success(user);
        }
        catch (Exception ex)
        {
            _chainTracing.RecordException(traceId, ex);
            _chainTracing.EndTrace(traceId, "Failed", false);
            throw;
        }

        // 方式二：使用扩展方法（推荐）
        return await _chainTracing.ExecuteWithTraceAsync("UserService", "GetUser", 
            async () =>
            {
                var user = await GetUserFromDatabase(userId);
                return ServiceResponse.Success(user);
            }, 
            new { UserId = userId });

        // 方式三：使用作用域（推荐）
        using var scope = _chainTracing.BeginScope("UserService", "GetUser", new { UserId = userId });
        try
        {
            var user = await GetUserFromDatabase(userId);
            scope.RecordSuccess("User found");
            return ServiceResponse.Success(user);
        }
        catch (Exception ex)
        {
            scope.RecordException(ex);
            throw;
        }
    }
}
```

### 2. 记录常见调用类型

```csharp
public class UserRepository
{
    private readonly IMoChainTracing _chainTracing;

    public UserRepository(IMoChainTracing chainTracing)
    {
        _chainTracing = chainTracing;
    }

    public async Task<User> GetUserAsync(int userId)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var user = await _dbContext.Users.FindAsync(userId);
            
            // 记录数据库调用
            _chainTracing.RecordDatabaseCall(
                operation: "SELECT",
                tableName: "Users",
                success: user != null,
                duration: stopwatch.Elapsed,
                extraInfo: new { UserId = userId }
            );
            
            return user;
        }
        catch (Exception ex)
        {
            _chainTracing.RecordDatabaseCall(
                operation: "SELECT",
                tableName: "Users",
                success: false,
                duration: stopwatch.Elapsed
            );
            throw;
        }
    }

    public async Task<string> GetUserCacheAsync(int userId)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var cacheKey = $"user:{userId}";
            var result = await _redis.GetStringAsync(cacheKey);
            
            // 记录 Redis 调用
            _chainTracing.RecordRedisCall(
                operation: "GET",
                key: cacheKey,
                success: result != null,
                duration: stopwatch.Elapsed
            );
            
            return result;
        }
        catch (Exception ex)
        {
            _chainTracing.RecordRedisCall(
                operation: "GET",
                key: $"user:{userId}",
                success: false,
                duration: stopwatch.Elapsed
            );
            throw;
        }
    }

    public async Task<ExternalUserInfo> GetExternalUserInfoAsync(int userId)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await _httpClient.GetAsync($"/api/users/{userId}");
            
            // 记录外部 API 调用
            _chainTracing.RecordExternalApiCall(
                serviceName: "UserAPI",
                endpoint: $"/api/users/{userId}",
                method: "GET",
                statusCode: (int)response.StatusCode,
                success: response.IsSuccessStatusCode,
                duration: stopwatch.Elapsed
            );
            
            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ExternalUserInfo>(content);
        }
        catch (Exception ex)
        {
            _chainTracing.RecordExternalApiCall(
                serviceName: "UserAPI",
                endpoint: $"/api/users/{userId}",
                method: "GET",
                success: false,
                duration: stopwatch.Elapsed
            );
            throw;
        }
    }
}
```

### 3. 领域服务调用追踪

```csharp
public class UserDomainService
{
    private readonly IMoChainTracing _chainTracing;

    public UserDomainService(IMoChainTracing chainTracing)
    {
        _chainTracing = chainTracing;
    }

    public async Task<bool> ValidateUserAsync(User user)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var isValid = await PerformValidation(user);
            
            // 记录领域服务调用
            _chainTracing.RecordDomainServiceCall(
                serviceName: "UserDomainService",
                methodName: "ValidateUser",
                success: true,
                duration: stopwatch.Elapsed,
                result: isValid ? "Valid" : "Invalid",
                extraInfo: new { UserId = user.Id, IsValid = isValid }
            );
            
            return isValid;
        }
        catch (Exception ex)
        {
            _chainTracing.RecordDomainServiceCall(
                serviceName: "UserDomainService",
                methodName: "ValidateUser",
                success: false,
                duration: stopwatch.Elapsed,
                result: "Exception",
                extraInfo: new { UserId = user.Id, Error = ex.Message }
            );
            throw;
        }
    }
}
```

### 4. 控制器中自动追踪

控制器中的调用链追踪是自动的，但你也可以手动附加调用链信息：

```csharp
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly IMoChainTracing _chainTracing;

    public UsersController(IUserService userService, IMoChainTracing chainTracing)
    {
        _userService = userService;
        _chainTracing = chainTracing;
    }

    [HttpGet("{id}")]
    public async Task<ServiceResponse<User>> GetUser(int id)
    {
        var result = await _userService.GetUserAsync(id);
        
        // 手动附加调用链信息（可选，ActionFilter 会自动处理）
        return result.WithChainTracing(_chainTracing);
    }

    // 跳过调用链追踪
    [HttpGet("health")]
    [SkipChainTracing]
    public IActionResult Health()
    {
        return Ok("Healthy");
    }
}
```

### 5. 微服务调用链合并

在微服务架构中，调用链追踪需要跨服务传递和合并。模块提供了自动合并远程调用链的功能：

```csharp
public class OrderService
{
    private readonly IMoChainTracing _chainTracing;
    private readonly HttpClient _httpClient;

    public OrderService(IMoChainTracing chainTracing, HttpClient httpClient)
    {
        _chainTracing = chainTracing;
        _httpClient = httpClient;
    }

    // 方式一：自动合并微服务调用链
    public async Task<ServiceResponse<Order>> CreateOrderAsync(CreateOrderRequest request)
    {
        return await _chainTracing.ExecuteWithMicroserviceTraceAsync("UserService", "ValidateUser", 
            async () =>
            {
                var response = await _httpClient.PostAsync("/api/users/validate", content);
                var result = await response.Content.ReadFromJsonAsync<ServiceResponse<UserValidation>>();
                return result; // 调用链信息会自动合并
            });
    }

    // 方式二：使用作用域进行微服务调用
    public async Task<ServiceResponse<Order>> ProcessOrderAsync(int orderId)
    {
        return await _chainTracing.ExecuteWithMicroserviceScopeAsync("PaymentService", "ProcessPayment",
            async (scope) =>
            {
                var response = await _httpClient.PostAsync($"/api/payments/process/{orderId}", content);
                var result = await response.Content.ReadFromJsonAsync<ServiceResponse<PaymentResult>>();
                
                // 调用链信息会自动合并到 scope 中
                return result;
            });
    }

    // 方式三：手动合并调用链
    public async Task<ServiceResponse<Order>> GetOrderDetailsAsync(int orderId)
    {
        var traceId = _chainTracing.BeginTrace("Microservice(InventoryService)", "CheckInventory");
        try
        {
            var response = await _httpClient.GetAsync($"/api/inventory/check/{orderId}");
            var result = await response.Content.ReadFromJsonAsync<ServiceResponse<InventoryStatus>>();
            
            // 手动合并远程调用链
            if (result.Metadata != null)
            {
                _chainTracing.MergeRemoteChain(traceId, result);
            }
            
            _chainTracing.EndTrace(traceId, $"Status: {result.Status}", result.Status == ResultStatus.Ok);
            return result;
        }
        catch (Exception ex)
        {
            _chainTracing.RecordException(traceId, ex);
            _chainTracing.EndTrace(traceId, "Failed", false);
            throw;
        }
    }

    // 方式四：简单记录微服务调用
    public async Task<ServiceResponse<User>> GetUserInfoAsync(int userId)
    {
        var response = await _httpClient.GetAsync($"/api/users/{userId}");
        var result = await response.Content.ReadFromJsonAsync<ServiceResponse<User>>();
        
        // 记录微服务调用并自动合并调用链
        _chainTracing.RecordMicroserviceCall("UserService", "GetUser", result);
        
        return result;
    }
}
```

### 6. 跨服务调用链传递

如果需要在微服务调用中传递当前的调用链信息：

```csharp
public class ApiClient
{
    private readonly IMoChainTracing _chainTracing;
    private readonly HttpClient _httpClient;

    // 提取调用链信息用于传递
    public async Task<ServiceResponse<T>> CallServiceAsync<T>(string endpoint)
    {
        // 提取当前调用链信息
        var chainInfo = _chainTracing.ExtractChainForPropagation();
        
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        
        // 可以将调用链信息添加到请求头或请求体中
        if (chainInfo != null)
        {
            request.Headers.Add("X-Chain-Tracing", JsonSerializer.Serialize(chainInfo));
        }

        var response = await _httpClient.SendAsync(request);
        var result = await response.Content.ReadFromJsonAsync<ServiceResponse<T>>();
        
        return result;
    }
}
```

### 7. 特性标记

```csharp
// 在特定控制器或 Action 上启用调用链追踪
[ChainTracing]
public class SpecialController : ControllerBase
{
    // ...
}

// 跳过调用链追踪
[SkipChainTracing]
public class HealthController : ControllerBase
{
    // ...
}
```

## 禁用调用链追踪

在某些场景下（如生产环境、性能测试等），您可能需要禁用调用链追踪功能。模块提供了多种禁用方式：

### 1. 直接禁用

```csharp
// 方式一：使用扩展方法禁用
builder.Services.DisableChainTracing();

// 方式二：使用完整的禁用方法
builder.Services.DisableMoChainTracing();
```

### 2. 条件性启用/禁用

```csharp
// 根据环境条件
builder.Services.AddChainTracing(enabled: !builder.Environment.IsProduction());

// 根据配置文件
builder.Services.AddChainTracing(enabled: builder.Configuration.GetValue<bool>("Features:EnableChainTracing"));

// 使用条件函数
builder.Services.AddChainTracing(() => 
{
    var config = builder.Configuration;
    return config.GetValue<bool>("Features:EnableChainTracing") && 
           !builder.Environment.IsProduction();
});
```

### 3. 通过配置选项禁用

```csharp
builder.Services.AddMoChainTracing(options =>
{
    options.EnableAutoTracing = false; // 这将自动注册空实现
    options.EnableMiddleware = false;
    options.EnableActionFilter = false;
});
```

### 4. EmptyChainTracing 实现

当禁用调用链追踪时，系统会自动注册 `EmptyChainTracing` 实现，它采用空对象模式（Null Object Pattern）：

- 所有追踪方法都是无操作的
- 不会产生任何性能开销
- 不会创建任何调用链对象
- 不会占用额外内存
- 所有扩展方法仍然可以正常调用

```csharp
// 即使禁用了调用链追踪，这些代码仍然可以正常运行，只是不会产生任何追踪信息
await _chainTracing.ExecuteWithTraceAsync("Service", "Method", async () =>
{
    // 业务逻辑
});

using var scope = _chainTracing.BeginScope("Service", "Method");
// 业务逻辑
scope.RecordSuccess();
```

### 5. 配置文件示例

```json
{
  "Features": {
    "EnableChainTracing": false
  },
  "ChainTracing": {
    "EnableAutoTracing": false,
    "EnableMiddleware": false,
    "EnableActionFilter": false
  }
}
```

### 6. 性能对比

| 实现 | 内存占用 | CPU 开销 | 响应体大小 |
|------|----------|----------|------------|
| AsyncLocalMoChainTracing | 中等 | 低 | 增加 |
| EmptyChainTracing | 极低 | 无 | 无影响 |

## 配置选项

```csharp
builder.Services.AddMoChainTracing(options =>
{
    // 是否启用自动调用链追踪
    options.EnableAutoTracing = true;
    
    // 是否启用中间件
    options.EnableMiddleware = true;
    
    // 是否启用 ActionFilter
    options.EnableActionFilter = true;
    
    // 是否记录请求参数
    options.LogRequestParameters = false;
    
    // 是否记录响应内容
    options.LogResponseContent = false;
    
    // 最大调用链深度（防止无限递归）
    options.MaxChainDepth = 50;
    
    // 最大节点数量（防止内存泄漏）
    options.MaxNodeCount = 1000;
    
    // 是否在 Metadata 中包含调用链的详细信息
    options.IncludeDetailedChainInfo = true;
    
    // 是否在 Metadata 中包含调用链的汇总信息
    options.IncludeChainSummary = true;
    
    // 需要跳过的路径模式
    options.SkipPathPatterns.Add(@"^/api/health.*");
    
    // 需要跳过的控制器名称
    options.SkipControllers.Add("DiagnosticsController");
});
```

## 响应格式

调用链信息会自动附加到 `IResultEnvelope.Metadata` 中，支持微服务调用链的合并：

```json
{
  "code": "Ok",
  "message": "Success",
  "data": { /* 业务数据 */ },
  "extraInfo": {
    "chainTracing": {
      "totalDurationMs": 256.78,
      "startTime": "2024-01-01T10:00:00.000Z",
      "endTime": "2024-01-01T10:00:00.256Z",
      "rootNode": {
        "traceId": "abc123def456",
        "handler": "HTTP",
        "operation": "GET /api/orders/1",
        "startTime": "2024-01-01T10:00:00.000Z",
        "endTime": "2024-01-01T10:00:00.256Z",
        "durationMs": 256.78,
        "success": true,
        "isRemoteCall": false,
        "children": [
          {
            "traceId": "def456ghi789",
            "handler": "Controller(OrdersController)",
            "operation": "GetOrder",
            "durationMs": 245.12,
            "success": true,
            "isRemoteCall": false,
            "children": [
              {
                "traceId": "ghi789jkl012",
                "handler": "OrderService",
                "operation": "GetOrderAsync",
                "durationMs": 220.45,
                "success": true,
                "isRemoteCall": false,
                "children": [
                  {
                    "handler": "Database",
                    "operation": "SELECT(Orders)",
                    "durationMs": 25.23,
                    "success": true,
                    "isRemoteCall": false,
                    "result": "Success, Rows: 1"
                  },
                  {
                    "traceId": "jkl012mno345",
                    "handler": "Microservice(UserService)",
                    "operation": "GetUser",
                    "durationMs": 180.34,
                    "success": true,
                    "isRemoteCall": false,
                    "children": [
                      {
                        "traceId": "mno345pqr678",
                        "handler": "HTTP",
                        "operation": "GET /api/users/123",
                        "durationMs": 175.22,
                        "success": true,
                        "isRemoteCall": true,
                        "children": [
                          {
                            "traceId": "pqr678stu901",
                            "handler": "Controller(UsersController)",
                            "operation": "GetUser",
                            "durationMs": 165.11,
                            "success": true,
                            "isRemoteCall": true,
                            "children": [
                              {
                                "handler": "Database",
                                "operation": "SELECT(Users)",
                                "durationMs": 45.67,
                                "success": true,
                                "isRemoteCall": true,
                                "result": "Success, Rows: 1"
                              },
                              {
                                "handler": "Redis",
                                "operation": "GET(user:123)",
                                "durationMs": 12.44,
                                "success": true,
                                "isRemoteCall": true,
                                "result": "Success"
                              }
                            ]
                          }
                        ]
                      }
                    ]
                  }
                ]
              }
            ]
          }
        ]
      },
      "summary": {
        "totalNodes": 9,
        "successfulNodes": 9,
        "failedNodes": 0,
        "activeNodes": 0,
        "remoteNodes": 4,
        "localNodes": 5
      }
    }
  }
}
```

### 调用链合并说明

- **`isRemoteCall`**: 标识节点是否来自远程微服务调用
- **远程调用链**: 自动合并到当前调用链的层次结构中
- **节点统计**: 汇总信息包含本地节点和远程节点的统计
- **完整追踪**: 可以看到从 HTTP 请求到跨微服务调用的完整链路

## 最佳实践

1. **使用扩展方法**：优先使用 `ExecuteWithTraceAsync` 等扩展方法，代码更简洁。

2. **作用域模式**：对于复杂的调用流程，使用 `BeginScope` 作用域模式。

3. **合理的追踪粒度**：不要过度追踪，关注关键的业务流程和外部调用。

4. **配置跳过规则**：为健康检查、静态资源等端点配置跳过规则。

5. **监控内存使用**：在高并发环境中注意调用链的内存占用，合理设置 `MaxChainDepth` 和 `MaxNodeCount`。

6. **异常处理**：确保在异常情况下也能正确记录调用链信息。

## 与原有实现的区别

与基于 `MoRequestContext` 的原有实现相比，新的 `MoChainTracing` 模块具有以下优势：

- ✅ 基于 `AsyncLocal`，不依赖 `HttpContext`
- ✅ 更简洁的 API 设计
- ✅ 更好的异常处理
- ✅ 更灵活的配置选项
- ✅ 支持多种使用模式（扩展方法、作用域、手动）
- ✅ 更好的性能和内存管理

## 注意事项

1. `AsyncLocal` 在某些情况下可能会导致内存泄漏，注意监控内存使用。
2. 调用链追踪会增加一定的性能开销，在高性能要求的场景中需要权衡。
3. 调用链信息会增加响应体大小，可以根据需要配置包含的信息级别。
4. 在微服务环境中，可能需要额外的配置来支持跨服务的调用链追踪。 
