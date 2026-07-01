using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GuiEcad.Model;
using GuiEcad.Rendering;
using GuiEcad.Simulation;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinColor = Windows.UI.Color;

namespace GuiEcad_App;

internal sealed partial class PdfPreviewDialog : ContentDialog
{
    private enum PageKind { Sheet, CrossRef, Bom }

    private sealed record PreviewPage(
        PageKind Kind,
        Sheet? Sheet,
        int PageRowStart,
        int PageNumber,
        int TotalPages,
        int CrPageIndex
    );

    private readonly LadderDocument _document;
    private readonly CrossReference _xref;
    private readonly bool _enableBorder;
    private readonly DiagramRenderer _dr;
    private readonly List<PreviewPage> _pages = new();

    private int _currentIndex;
    private float _zoom = 1.0f;

    public PdfPreviewDialog(LadderDocument document, CrossReference xref, bool enableBorder)
    {
        _document = document;
        _xref = xref;
        _enableBorder = enableBorder;
        _dr = new DiagramRenderer(DrawingTheme.Default, new RenderOptions());

        InitializeComponent();

        Resources["ContentDialogMaxWidth"] = 920.0;
        Resources["ContentDialogMaxHeight"] = 800.0;

        BuildPageList();
        UpdateUI();
    }

    private void BuildPageList()
    {
        int sheetPages = _enableBorder
            ? _document.Sheets.Sum(_dr.RenderPageCount)
            : _document.Sheets.Count;
        int crPages = _dr.CrossRefPageCount(_xref);
        bool hasBom = _document.Devices.ByName.Count > 0;
        int totalPages = sheetPages + crPages + (hasBom ? 1 : 0);

        int physical = 0;
        foreach (var sheet in _document.Sheets)
        {
            int pages = _enableBorder ? _dr.RenderPageCount(sheet) : 1;
            for (int p = 0; p < pages; p++)
            {
                physical++;
                _pages.Add(new PreviewPage(
                    PageKind.Sheet, sheet,
                    PageRowStart: p * DiagramRenderer.RowsPerPage,
                    PageNumber: physical,
                    TotalPages: totalPages,
                    CrPageIndex: 0
                ));
            }
        }
        for (int cp = 0; cp < crPages; cp++)
        {
            physical++;
            _pages.Add(new PreviewPage(
                PageKind.CrossRef, Sheet: null,
                PageRowStart: 0, PageNumber: physical,
                TotalPages: totalPages, CrPageIndex: cp
            ));
        }
        if (hasBom)
        {
            physical++;
            _pages.Add(new PreviewPage(
                PageKind.Bom, Sheet: null,
                PageRowStart: 0, PageNumber: physical,
                TotalPages: totalPages, CrPageIndex: 0
            ));
        }
    }

    private Size2D GetPageSize(PreviewPage page) => page.Kind switch
    {
        PageKind.Sheet    => _dr.PageSize(page.Sheet!, xref: null, info: _document.Info, enableBorder: _enableBorder),
        PageKind.CrossRef => _dr.CrossRefPageSize(),
        PageKind.Bom      => _dr.BomPageSize(_document.Sheets[^1].Grid.Columns, _document.Devices.ByName.Count),
        _                 => _dr.CrossRefPageSize(),
    };

    // ===== UI 更新 =====

    private void UpdateUI()
    {
        int total = _pages.Count;
        PageLabel.Text = total == 0 ? "ページなし" : $"{_currentIndex + 1} / {total} ページ";
        PrevBtn.IsEnabled = _currentIndex > 0;
        NextBtn.IsEnabled = _currentIndex < total - 1;
        ZoomLabel.Text = $"{_zoom * 100:F0}%";
        ZoomOutBtn.IsEnabled = _zoom > 0.25f + 0.01f;
        ZoomInBtn.IsEnabled = _zoom < 4.0f - 0.01f;

        RefreshCanvas();
    }

    private void RefreshCanvas()
    {
        if (_pages.Count == 0) return;
        UpdateCanvasHeight();
        PreviewCanvas.Invalidate();
    }

    private void UpdateCanvasHeight()
    {
        double width = Math.Max(PreviewCanvas.ActualWidth, 820);
        var ps = GetPageSize(_pages[_currentIndex]);
        double scale = _zoom * (width - 40) / ps.Width;
        PreviewCanvas.Height = ps.Height * scale + 40;
    }

    // ===== ページ操作 =====

    private void OnPrev(object sender, RoutedEventArgs e)
    {
        if (_currentIndex <= 0) return;
        _currentIndex--;
        UpdateUI();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_currentIndex >= _pages.Count - 1) return;
        _currentIndex++;
        UpdateUI();
    }

    // ===== ズーム操作 =====

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        _zoom = Math.Max(0.25f, MathF.Round(_zoom - 0.25f, 2));
        UpdateUI();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        _zoom = Math.Min(4.0f, MathF.Round(_zoom + 0.25f, 2));
        UpdateUI();
    }

    private void OnFitWidth(object sender, RoutedEventArgs e)
    {
        _zoom = 1.0f;
        UpdateUI();
    }

    // ===== キャンバス =====

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pages.Count == 0) return;
        UpdateCanvasHeight();
        PreviewCanvas.Invalidate();
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_pages.Count == 0 || sender.ActualWidth < 1) return;

        var session = args.DrawingSession;
        var page = _pages[_currentIndex];
        var ps = GetPageSize(page);

        float availW = (float)sender.ActualWidth;
        float scale  = _zoom * (availW - 40f) / (float)ps.Width;
        float pw     = (float)ps.Width  * scale;
        float ph     = (float)ps.Height * scale;
        float ox     = (availW - pw) / 2f;
        float oy     = 20f;

        // ドロップシャドウ
        session.FillRectangle(ox + 3, oy + 3, pw, ph,
            WinColor.FromArgb(60, 0, 0, 0));
        // 白紙
        session.FillRectangle(ox, oy, pw, ph, Colors.White);

        // DiagramRenderer でページ内容を描画（mm座標 → DIP 変換）
        var transform = Matrix3x2.CreateScale(scale)
                      * Matrix3x2.CreateTranslation(ox, oy);
        var renderer = new Win2DRenderer(session, transform);

        switch (page.Kind)
        {
            case PageKind.Sheet:
                _dr.Render(renderer, page.Sheet!, _document.Library, sim: null,
                           xref: null, info: _document.Info,
                           pageNumber: page.PageNumber, totalPages: page.TotalPages,
                           enableBorder: _enableBorder,
                           pageRowStart: page.PageRowStart,
                           pageRowCount: DiagramRenderer.RowsPerPage);
                break;
            case PageKind.CrossRef:
                _dr.RenderCrossRefPage(renderer, _xref, page.CrPageIndex);
                break;
            case PageKind.Bom:
                _dr.RenderBomPage(renderer, _document.Devices,
                                  _document.Sheets[^1].Grid.Columns);
                break;
        }

        // キャンバス高さをページ内容に合わせる（収束後は変化しない）
        double newH = ph + oy + 20;
        if (Math.Abs(sender.Height - newH) > 0.5)
            sender.Height = newH;
    }
}
