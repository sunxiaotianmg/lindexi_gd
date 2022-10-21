﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using LightTextEditorPlus.Core.Document;
using LightTextEditorPlus.Core.Document.Segments;
using LightTextEditorPlus.Core.Exceptions;
using LightTextEditorPlus.Core.Platform;
using LightTextEditorPlus.Core.Primitive;
using LightTextEditorPlus.Core.Primitive.Collections;
using TextEditor = LightTextEditorPlus.Core.TextEditorCore;

namespace LightTextEditorPlus.Core.Layout;

// todo 支持输出排版信息，渲染信息。如每一行，每个字符的坐标和尺寸
// todo 文本公式混排 文本图片混排 文本和其他元素的混排多选 文本和其他可交互元素混排的光标策略
class LayoutManager
{
    public LayoutManager(TextEditor textEditor)
    {
        TextEditor = textEditor;
    }

    public TextEditor TextEditor { get; }
    public event EventHandler? InternalLayoutCompleted;

    public void UpdateLayout()
    {
        if (ArrangingLayoutProvider?.ArrangingType != TextEditor.ArrangingType)
        {
            ArrangingLayoutProvider = TextEditor.ArrangingType switch
            {
                ArrangingType.Horizontal => new HorizontalArrangingLayoutProvider(TextEditor),
                // todo 支持竖排文本
                _ => throw new NotSupportedException()
            };
        }

        var result = ArrangingLayoutProvider.UpdateLayout();
        DocumentRenderData.DocumentBounds = result.DocumentBounds;
        DocumentRenderData.IsDirty = false;

        InternalLayoutCompleted?.Invoke(this, EventArgs.Empty);
    }

    private ArrangingLayoutProvider? ArrangingLayoutProvider { set; get; }

    public DocumentRenderData DocumentRenderData { get; } = new DocumentRenderData()
    {
        IsDirty = true,
    };
}

/// <summary>
/// 水平方向布局的提供器
/// </summary>
class HorizontalArrangingLayoutProvider : ArrangingLayoutProvider
{
    public HorizontalArrangingLayoutProvider(TextEditorCore textEditor) : base(textEditor)
    {
    }

    public override ArrangingType ArrangingType => ArrangingType.Horizontal;

    /// <summary>
    /// 布局段落的核心逻辑
    /// </summary>
    /// <param name="argument"></param>
    /// <param name="startParagraphOffset"></param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <remarks>
    /// 逻辑上是：
    /// 布局按照： 文本-段落-行-Run-字符
    /// 布局整个文本
    /// 布局文本的每个段落 <see cref="LayoutParagraphCore"/>
    /// 段落里面，需要对每一行进行布局 <see cref="LayoutWholeLine"/>
    /// 每一行里面，需要对每个 Char 字符进行布局 <see cref="LayoutSingleCharInLine"/>
    /// 每个字符需要调用平台的测量 <see cref="MeasureCharInfo"/>
    /// </remarks>
    protected override ParagraphLayoutResult LayoutParagraphCore(ParagraphLayoutArgument argument,
        ParagraphOffset startParagraphOffset)
    {
        //// 当前行的 RunList 列表，看起来设计不对，没有加上在段落的坐标
        //var currentLineRunList = new List<IImmutableRun>();
        // 当前的行渲染信息
        LineVisualData? currentLineVisualData = null;

        var paragraph = argument.ParagraphData;

        // 获取最大宽度信息
        double lineMaxWidth = TextEditor.SizeToContent switch
        {
            SizeToContent.Manual => TextEditor.DocumentManager.DocumentWidth,
            SizeToContent.Width => double.PositiveInfinity,
            SizeToContent.Height => TextEditor.DocumentManager.DocumentWidth,
            SizeToContent.WidthAndHeight => double.PositiveInfinity,
            _ => throw new ArgumentOutOfRangeException()
        };

        var wholeRunLineLayouter = TextEditor.PlatformProvider.GetWholeRunLineLayouter();

        for (var i = startParagraphOffset.Offset; i < paragraph.CharCount;)
        {
            // 开始行布局
            // 第一个 Run 就是行的开始
            ReadOnlyListSpan<CharData> charDataList = paragraph.ToReadOnlyListSpan(i);
            WholeRunLineLayoutResult result;
            if (wholeRunLineLayouter != null)
            {
                var wholeRunLineLayoutArgument = new WholeRunLineLayoutArgument(paragraph.ParagraphProperty, charDataList, lineMaxWidth);
                result = wholeRunLineLayouter.LayoutWholeRunLine(wholeRunLineLayoutArgument);
            }
            else
            {
                // 继续往下执行，如果没有注入自定义的行布局层的话
                result = LayoutWholeLine(paragraph, charDataList, lineMaxWidth);
            }

            currentLineVisualData = new LineVisualData(paragraph)
            {
                StartParagraphIndex = i,
                EndParagraphIndex = i + result.RunCount,
                Size = result.Size,
                //LeftTop = 等待所有行完成了，再赋值
            };

            paragraph.LineVisualDataList.Add(currentLineVisualData);

            i += result.RunCount;

            if (result.RunCount == 0)
            {
                if (TextEditor.IsInDebugMode)
                {
                    throw new TextEditorDebugException($"某一行在布局时，只采用了零个字符");
                }

                // todo 理论上不可能，表示行布局出错了
                TextEditor.Logger.LogWarning($"某一行在布局时，只采用了零个字符");
                throw new NotImplementedException();
            }
        }

        // todo 考虑行复用，例如刚好添加的内容是一行。或者在一行内做文本替换等
        // 这个没有啥优先级。测试了 SublimeText 和 NotePad 工具，都没有做此复用，预计有坑

        // 布局左上角坐标
        var leftTop = argument.CurrentStartPoint;

        var paragraphSize = new Size(0, 0);

        foreach (var lineVisualData in argument.ParagraphData.LineVisualDataList)
        {
            lineVisualData.LeftTop = leftTop;

            var width = Math.Max(paragraphSize.Width, lineVisualData.Size.Width);
            var height = paragraphSize.Height + lineVisualData.Size.Height;

            paragraphSize = new Size(width, height);

            leftTop = new Point(leftTop.X, leftTop.Y + lineVisualData.Size.Height);

            lineVisualData.UpdateVersion();
        }

        argument.ParagraphData.ParagraphRenderData.Size = paragraphSize;

        paragraph.IsDirty = false;

        return new ParagraphLayoutResult(leftTop);
    }

    private WholeRunLineLayoutResult LayoutWholeLine(ParagraphData paragraph, ReadOnlyListSpan<CharData> charDataList,
        double lineMaxWidth)
    {
        var singleRunLineLayouter = TextEditor.PlatformProvider.GetSingleRunLineLayouter();

        // RunLineMeasurer
        // 行还剩余的空闲宽度
        double lineRemainingWidth = lineMaxWidth;

        int currentRunIndex = 0;
        var currentSize = Size.Zero;

        while (currentRunIndex < charDataList.Count)
        {
            // 一行里面需要逐个字符进行布局
            var arguments = new SingleCharInLineLayoutArguments(charDataList, currentRunIndex, lineRemainingWidth,
                paragraph.ParagraphProperty);

            SingleCharInLineLayoutResult result;
            if (singleRunLineLayouter is not null)
            {
                result = singleRunLineLayouter.LayoutSingleRunInLine(arguments);
            }
            else
            {
                result = LayoutSingleCharInLine(arguments);
            }

            if (result.CanTake)
            {
                var width = currentSize.Width + result.TotalSize.Width;
                var height = Math.Max(currentSize.Height, result.TotalSize.Height);
                currentSize = new Size(width, height);

                if (result.TaskCount == 1)
                {
                    var charData = charDataList[currentRunIndex];
                    charData.Size ??= result.TotalSize;
                }
                else
                {
                    for (int i = currentRunIndex; i < currentRunIndex + result.TaskCount; i++)
                    {
                        var charData = charDataList[i];
                        //charData.CharRenderData ??=
                        //    new CharRenderData(charData, paragraph);
                        charData.Size ??= result.CharSizeList![i - currentRunIndex];
                    }
                }

                currentRunIndex += result.TaskCount;
            }

            if (result.ShouldBreakLine)
            {
                // 换行，这一行布局完成
                break;
            }
        }

        return new WholeRunLineLayoutResult(currentSize, currentRunIndex);
    }

    private SingleCharInLineLayoutResult LayoutSingleCharInLine(SingleCharInLineLayoutArguments arguments)
    {
        var charInfoMeasurer = TextEditor.PlatformProvider.GetCharInfoMeasurer();

        var charData = arguments.RunList[arguments.CurrentIndex];

        var cacheSize = charData.Size;

        Size size;
        if (cacheSize == null)
        {
            var charInfo = new CharInfo(charData.CharObject, charData.RunProperty);
            CharInfoMeasureResult charInfoMeasureResult;
            if (charInfoMeasurer != null)
            {
                charInfoMeasureResult = charInfoMeasurer.MeasureCharInfo(charInfo);
            }
            else
            {
                charInfoMeasureResult = MeasureCharInfo(charInfo);
            }

            size = charInfoMeasureResult.Bounds.Size;
        }
        else
        {
            size = cacheSize.Value;
        }

        if (arguments.LineRemainingWidth > size.Width)
        {
            return new SingleCharInLineLayoutResult(taskCount: 1, size, charSizeList: default);
        }
        else
        {
            // 如果尺寸不足，也就是一个都拿不到
            return new SingleCharInLineLayoutResult(taskCount: 0, default, charSizeList: default);
        }
    }

    private CharInfoMeasureResult MeasureCharInfo(CharInfo charInfo)
    {
        var bounds = new Rect(0, 0, charInfo.RunProperty.FontSize, charInfo.RunProperty.FontSize);
        return new CharInfoMeasureResult(bounds);
    }
}

/// <summary>
/// 实际的布局提供器
/// </summary>
abstract class ArrangingLayoutProvider
{
    protected ArrangingLayoutProvider(TextEditor textEditor)
    {
        TextEditor = textEditor;
    }

    public abstract ArrangingType ArrangingType { get; }
    public TextEditor TextEditor { get; }

    public DocumentLayoutResult UpdateLayout()
    {
        // todo 项目符号的段落，如果在段落上方新建段落，那需要项目符号更新
        // 这个逻辑准备给项目符号逻辑更新，逻辑是，假如现在有两段，分别采用 `1. 2.` 作为项目符号
        // 在 `1.` 后面新建一个段落，需要自动将原本的 `2.` 修改为 `3.` 的内容，这个逻辑准备交给项目符号模块自己编辑实现

        // 布局逻辑：
        // - 获取需要更新布局段落的逻辑
        // - 进入段落布局
        //   - 进入行布局
        // - 获取文档整个的布局信息
        //   - 获取文档的布局尺寸

        // 首行出现变脏的序号
        var firstDirtyParagraphIndex = -1;
        var paragraphList = TextEditor.DocumentManager.TextRunManager.ParagraphManager.GetParagraphList();
        for (var index = 0; index < paragraphList.Count; index++)
        {
            ParagraphData paragraphData = paragraphList[index];
            if (paragraphData.IsDirty)
            {
                firstDirtyParagraphIndex = index;
                break;
            }
        }


        if (firstDirtyParagraphIndex == -1)
        {
            throw new NotImplementedException($"进入布局时，没有任何一段需要布局");
        }

        //// 进入各个段落的段落之间和行之间的布局
        // 获取首个脏段的左上角的点
        Point firstLeftTop;
        if (firstDirtyParagraphIndex == 0)
        {
            // 从首段落开始
            firstLeftTop=new Point(0, 0);
        }
        else
        {
            // todo 获取非首段的左上角坐标
            //firstLeftTop = list[firstDirtyParagraphIndex - 1].ParagraphRenderData.LeftTop;
            throw new NotImplementedException();
        }

        // 进入段落内布局
        var currentLeftTop = firstLeftTop;
        for (var index = firstDirtyParagraphIndex; index < paragraphList.Count; index++)
        {
            ParagraphData paragraphData = paragraphList[index];

            var argument = new ParagraphLayoutArgument(index, currentLeftTop, paragraphData, paragraphList);

            ParagraphLayoutResult result = LayoutParagraph(argument);
            currentLeftTop = result.CurrentLeftTop;
        }

        var documentBounds = Rect.Zero;
        foreach (var paragraphData in paragraphList)
        {
            var bounds = paragraphData.ParagraphRenderData.GetBounds();
            documentBounds = documentBounds.Union(bounds);
        }

        Debug.Assert(TextEditor.DocumentManager.TextRunManager.ParagraphManager.GetParagraphList()
            .All(t => t.IsDirty == false));

        return new DocumentLayoutResult(documentBounds);
    }

    /// <summary>
    /// 段落内布局
    /// </summary>
    private ParagraphLayoutResult LayoutParagraph(ParagraphLayoutArgument argument)
    {
        // 先找到首个需要更新的坐标点，这里的坐标是段坐标
        var dirtyParagraphOffset = 0;
        var lastIndex = -1;
        var paragraph = argument.ParagraphData;
        for (var index = 0; index < paragraph.LineVisualDataList.Count; index++)
        {
            LineVisualData lineVisualData = paragraph.LineVisualDataList[index];
            if (lineVisualData.IsDirty == false)
            {
                dirtyParagraphOffset += lineVisualData.CharCount;
            }
            else
            {
                lastIndex = index;
                break;
            }
        }

        if (lastIndex > 0)
        {
            // todo 考虑行信息的复用，或者调用释放方法
            paragraph.LineVisualDataList.RemoveRange(lastIndex, paragraph.LineVisualDataList.Count - lastIndex);
        }

        // 不需要通过如此复杂的逻辑获取有哪些，因为存在的坑在于后续分拆 IImmutableRun 逻辑将会复杂
        //paragraph.GetRunRange(dirtyParagraphOffset);

        if (paragraph.CharCount == 0)
        {
            // todo 考虑 paragraph.TextRunList 数量为空的情况，只有一个换行的情况
        }

        var startParagraphOffset = new ParagraphOffset(dirtyParagraphOffset);

       var result = LayoutParagraphCore(argument, startParagraphOffset);

       return result;
    }

    protected abstract ParagraphLayoutResult LayoutParagraphCore(ParagraphLayoutArgument paragraph,
        ParagraphOffset startParagraphOffset);
}