# 分布式取消令牌管理器使用指南

## 概述

`ICancellationManager` 提供了跨微服务实例的分布式取消令牌管理功能。它使用 `IStateStore` 作为底层存储，实现了在微服务架构中统一管理和同步取消令牌状态。

## 功能特性

- **分布式同步**: 跨多个微服务实例同步取消令牌状态
- **自动轮询**: 后台轮询监听分布式状态变化
- **本地缓存**: 减少对底层存储的频繁访问
- **异常处理**: 完善的错误处理和日志记录
- **资源清理**: 自动清理不再使用的取消令牌资源

## 注册服务

### 1. 配置模块

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddMonica(monica =>
{
    monica.AddCancellationManager(options =>
    {
        options.UseDistributed = true;
        options.PollingIntervalMs = 2000;
        options.EnableVerboseLogging = true;
        options.StateTtl = TimeSpan.FromHours(48);
    });
});

var app = builder.Build();
```

### 2. 依赖注入

```csharp
public class YourService
{
    private readonly ICancellationManager _cancellationManager;
    
    public YourService(ICancellationManager cancellationManager)
    {
        _cancellationManager = cancellationManager;
    }
}
```

## 使用示例

### 替换现有的静态取消令牌

**改进后的代码（分布式方式）:**
```csharp
public class CommandHandlerTriggerRPLGen
{
    private const string RPL_GEN_TOKEN_KEY = "RPLGeneration";
    private readonly ICancellationManager _cancellationManager;
    
    public CommandHandlerTriggerRPLGen(ICancellationManager cancellationManager)
    {
        _cancellationManager = cancellationManager;
    }
    
    private async Task<bool> CanStartTriggerAsync()
    {
        return !(await _cancellationManager.IsCancelledAsync(RPL_GEN_TOKEN_KEY));
    }
    
    private async Task<bool> StartTriggerAsync()
    {
        if (await _cancellationManager.IsCancelledAsync(RPL_GEN_TOKEN_KEY))
        {
            return false;
        }
        
        // 重置并获取新的取消令牌
        await _cancellationManager.ResetTokenAsync(RPL_GEN_TOKEN_KEY);
        return true;
    }
    
    private async Task EndTriggerAsync()
    {
        await _cancellationManager.CancelTokenAsync(RPL_GEN_TOKEN_KEY);
    }
    
    public async Task<Result<ResponseTriggerRPLGen>> Handle(CommandTriggerRPLGen request, CancellationToken cancellationToken)
    {
        if (!(await CanStartTriggerAsync()))
        {
            if (request.ForceCancel is true)
            {
                await EndTriggerAsync();
                return Result.Success("已取消生成");
            }
            
            // 返回当前状态...
        }
        
        if (request.StartGen is true || request.ForceRegenerated is true)
        {
            await StartTriggerAsync();
            
            // 获取分布式取消令牌
            var distributedToken = await _cancellationManager.GetOrCreateTokenAsync(RPL_GEN_TOKEN_KEY, cancellationToken);
            
            // 将分布式取消令牌传递给后台任务
            await _backgroundJobManager.EnqueueAsync(new JobRPLGenArgs { CancellationToken = distributedToken });
            
            return new Result<ResponseTriggerRPLGen>(new ResponseTriggerRPLGen()
            {
                CanGetStatus = true
            })
            {
                Message = "已触发生成，再次执行可查看生成状态"
            };
        }
        
        // 其他逻辑...
    }
}
```

## API 参考

### ICancellationManager 接口

#### GetOrCreateTokenAsync
创建或获取指定键的分布式取消令牌。

#### CancelTokenAsync
取消指定键的分布式取消令牌，触发所有监听此键的微服务实例中的取消令牌。

#### IsCancelledAsync
检查指定键的取消令牌是否已被取消。

#### ResetTokenAsync
重置指定键的取消令牌状态，将已取消的令牌重置为未取消状态。

#### DeleteTokenAsync
删除指定键的取消令牌，清理不再需要的取消令牌资源。

#### GetActiveTokenKeysAsync
获取所有活动取消令牌的键列表。

#### CancelTokensAsync
批量取消多个取消令牌。

## 最佳实践

1. **使用有意义的键名**: 选择能够清楚表达业务含义的键名
2. **及时清理资源**: 任务完成后调用 `DeleteTokenAsync` 清理资源
3. **异常处理**: 总是处理 `OperationCanceledException`
4. **日志记录**: 在关键操作点添加适当的日志记录
5. **性能考虑**: 对于高频操作，考虑使用本地缓存策略
