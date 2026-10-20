using System;

namespace Aether.Exchange;

/// <summary>交易所异常基类。</summary>
public class ExchangeException : Exception
{
    public ExchangeException(string message) : base(message) { }
}

/// <summary>交易所业务错误（含错误码 + 错误信息）。</summary>
public class ExchangeBusinessException : ExchangeException
{
    public int ErrorId { get; }
    public string ErrorMsg { get; }

    public ExchangeBusinessException(int errorId, string errorMsg)
        : base($"交易所业务错误 [{errorId}]: {errorMsg}")
    {
        ErrorId = errorId;
        ErrorMsg = errorMsg;
    }
}
