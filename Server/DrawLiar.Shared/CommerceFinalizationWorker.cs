using Microsoft.Extensions.Logging;

namespace DrawLiar.Server;

public sealed class CommerceFinalizationWorker(ICommerceFinalizationRepository repository, CommerceService commerce,
    ILogger<CommerceFinalizationWorker> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("결제 소비 대기열을 읽지 못했습니다. 잠시 후 재시도합니다."); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!commerce.HasDurableFinalization) return;
        var pending = await repository.ClaimCommerceFinalizationsAsync(cancellationToken);
        foreach (var item in pending)
        {
            try { await commerce.RetryFinalizationAsync(item, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ApiException error) when (error.Code == "PaymentReviewRequired")
            {
                await repository.CompleteCommerceFinalizationAsync(Guid.Parse(item.Order.Order.OrderId), cancellationToken);
                logger.LogWarning("검토 대상 결제의 소비를 종료했습니다. 주문 {OrderId}", item.Order.Order.OrderId);
            }
            catch
            {
                logger.LogWarning("결제 소비를 재시도합니다. 주문 {OrderId}, 시도 {Attempts}", item.Order.Order.OrderId, item.Attempts);
                await repository.RetryCommerceFinalizationAsync(Guid.Parse(item.Order.Order.OrderId), item.Attempts, cancellationToken);
            }
        }
    }
}
