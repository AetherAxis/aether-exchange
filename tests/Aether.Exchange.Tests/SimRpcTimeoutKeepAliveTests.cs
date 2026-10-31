using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

using Aether.Exchange.Rpc;

namespace Aether.Exchange.Tests;

/// <summary>
/// 超时语义专项（2026-10-31）：**一次慢应答只失败该请求，不许拆掉整条链路**。
/// <para>
/// 为什么单独立一条：线上「botnoise 每 30 秒无故断一次」的链路是
/// 前置 RPC 请求超时 → <c>CallCoreAsync</c> 立刻 <c>TearDownWithEvent()</c> → 前置 Kick 客户端会话（90013）
/// → SDK 重连 → 再超时。旧行为把「偶发慢 op」放大成「对外可见的周期性断线」。
/// 现在的契约是：单次超时 = 一个 <c>90014</c> 异常，连接照旧可用；连续
/// <c>MaxConsecutiveTimeouts</c> 次才认作链路失效。
/// </para>
/// <para>
/// 用裸 TCP 假柜台而不是 <c>LoopbackSim</c>：这里要控制的正是「服务端收下请求但**不回**」，
/// 真柜台每个 op 都会立刻应答，造不出这个场景。
/// </para>
/// </summary>
public class SimRpcTimeoutKeepAliveTests
{
    /// <summary>假柜台：hello 立即应答；queryAccount 前 <paramref name="stallCount"/> 次故意不回。</summary>
    private sealed class StallingServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _loop;
        private readonly int _stallCount;
        private int _stalled;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public StallingServer(int stallCount)
        {
            _stallCount = stallCount;
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            while (true)
            {
                byte[] bytes;
                try { bytes = await SimFraming.ReadFrameAsync(stream); }
                catch { return; }

                var req = JsonSerializer.Deserialize<SimRequest>(bytes, SimJson.Options);
                if (req is null) return;

                if (req.Op == SimOps.Hello)
                {
                    await ReplyAsync(stream, req.Id, new SimHello(1));
                    continue;
                }

                // queryAccount：前 N 次装死（模拟「服务端卡在长 op 上」），之后正常应答
                if (req.Op == SimOps.QueryAccount && _stalled < _stallCount)
                {
                    _stalled++;
                    continue;
                }
                await ReplyAsync(stream, req.Id, new { });
            }
        }

        private static Task ReplyAsync<T>(NetworkStream stream, long id, T result)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new SimResponse(id, true, JsonSerializer.SerializeToElement(result, SimJson.Options), null),
                SimJson.Options);
            return SimFraming.WriteFrameAsync(stream, payload);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _loop.ConfigureAwait(false); } catch { }
        }
    }

    [Fact]
    public async Task 单次超时只失败该请求_连接保留且下一次照常成功()
    {
        await using var server = new StallingServer(stallCount: 1);
        using var client = new SimRpcClient("127.0.0.1", server.Port, requestTimeoutMs: 300);

        await client.ConnectAsync();

        var ex = await Assert.ThrowsAsync<ExchangeBusinessException>(() => client.QueryAccountAsync());
        Assert.Equal(SimWireErrors.Timeout, ex.ErrorId);
        Assert.True(client.IsAlive, "单次超时不得拆连接——拆了就是一次对外可见的断线");

        // 迟到/不下发的请求不影响后续调用
        var account = await client.QueryAccountAsync();
        Assert.True(client.IsAlive);
        Assert.NotNull(account);
    }

    [Fact]
    public async Task 连续三次超时才判定链路失效并拆除()
    {
        await using var server = new StallingServer(stallCount: 3);
        using var client = new SimRpcClient("127.0.0.1", server.Port, requestTimeoutMs: 200);

        await client.ConnectAsync();

        for (var i = 1; i <= 2; i++)
        {
            var ex = await Assert.ThrowsAsync<ExchangeBusinessException>(() => client.QueryAccountAsync());
            Assert.Equal(SimWireErrors.Timeout, ex.ErrorId);
            Assert.True(client.IsAlive, $"第 {i} 次超时不该拆连接");
        }

        await Assert.ThrowsAsync<ExchangeBusinessException>(() => client.QueryAccountAsync());
        Assert.False(client.IsAlive, "连续 3 次超时必须认账并拆链路，否则服务端卡死会把后续调用全部吊死");
    }
}
