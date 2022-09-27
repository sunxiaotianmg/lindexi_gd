﻿namespace LightTextEditorPlus.Core.Document;

/// <summary>
/// 表示一段文本，具有相同的属性定义
/// </summary>
public interface IRun
{
    /// <summary>
    /// 包含的字符数量
    /// </summary>
    int Count { get; }

    /// <summary>
    /// 获取某个字符
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    ICharObject GetChar(int index);

    /// <summary>
    /// 定义的样式，可为空。空代表着没有定义特别的样式
    /// </summary>
    IReadOnlyRunProperty? RunProperty { get; }
}