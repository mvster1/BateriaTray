using System;
using System.IO;
using System.Runtime.InteropServices;
using static BateriaTray.N;

namespace BateriaTray;

// Desenha o ícone da bandeja: [símbolo pequeno do dispositivo][número], tudo num único ícone
// (GDI puro, com alpha e antialiasing).
internal static unsafe class Render
{
    public const int GlyphNone = 0, GlyphMouse = 1, GlyphHeadset = 2, GlyphKeyboard = 3, GlyphOther = 4;

    public static string DigitFace = "Bahnschrift SemiBold Condensed";
    public static int DigitWeight = 600;

    public static int IconSize()
    {
        int sz = GetSystemMetrics(49 /*SM_CXSMICON*/);
        return sz < 16 ? 16 : sz;
    }

    // Fonte de ícones do Windows: Fluent (Win11) ou MDL2 (Win10). Glifos: mouse E962, headset E95B.
    static string _iconFace;
    static string IconFace => _iconFace ??=
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "SegoeIcons.ttf"))
            ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    // Desenha o texto no maior tamanho cuja "tinta" caiba em boxW x boxH e devolve a cobertura (0..255)
    // centralizada nessa caixa.
    static byte[] Fit(string text, string faceName, int weight, int boxW, int boxH)
    {
        int W = Math.Max(boxW, boxH) * 3;
        BITMAPINFOHEADER bi = default;
        bi.biSize = 40; bi.biWidth = W; bi.biHeight = -W; bi.biPlanes = 1; bi.biBitCount = 32;
        void* bits;
        nint dc = CreateCompatibleDC(0);
        nint bmp = CreateDIBSection(dc, &bi, 0, &bits, 0, 0);
        nint oldBmp = SelectObject(dc, bmp);
        SetBkMode(dc, 1 /*TRANSPARENT*/);
        SetTextColor(dc, 0x00FFFFFF);
        uint* px = (uint*)bits;

        int bx0 = 0, by0 = 0, bw = 0, bh = 0;
        fixed (char* t = text)
        fixed (char* face = faceName)
        {
            for (int h = boxH * 2; h >= 4; h--)
            {
                NativeMemory.Clear(px, (nuint)(W * W * 4));
                nint font = CreateFontW(-h, 0, 0, 0, weight, 0, 0, 0, 0, 0, 0, 4 /*ANTIALIASED_QUALITY*/, 0, face);
                nint oldFont = SelectObject(dc, font);
                RECT r = new() { left = 0, top = 0, right = W, bottom = W };
                DrawTextW(dc, t, text.Length, &r, 0x1 | 0x4 | 0x20 | 0x800 /*CENTER|VCENTER|SINGLELINE|NOPREFIX*/);
                GdiFlush();
                SelectObject(dc, oldFont);
                DeleteObject(font);

                int minX = W, minY = W, maxX = -1, maxY = -1;
                for (int y = 0; y < W; y++)
                    for (int x = 0; x < W; x++)
                        if ((px[y * W + x] & 0xFF) > 24)
                        {
                            if (x < minX) minX = x; if (x > maxX) maxX = x;
                            if (y < minY) minY = y; if (y > maxY) maxY = y;
                        }
                if (maxX < 0) continue;
                bx0 = minX; by0 = minY; bw = maxX - minX + 1; bh = maxY - minY + 1;
                if (bw <= boxW && bh <= boxH) break;
            }
        }

        var cov = new byte[boxW * boxH];
        int ox = (boxW - bw) / 2, oy = (boxH - bh + 1) / 2;
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                int dx = x + ox, dy = y + oy, sx = bx0 + x, sy = by0 + y;
                if ((uint)dx >= (uint)boxW || (uint)dy >= (uint)boxH || (uint)sx >= (uint)W || (uint)sy >= (uint)W) continue;
                cov[dy * boxW + dx] = (byte)(px[sy * W + sx] & 0xFF);
            }

        SelectObject(dc, oldBmp);
        DeleteObject(bmp);
        DeleteDC(dc);
        return cov;
    }

    // ---- Pixel art 16x16 (nítido em 100%): números 4x9 e símbolos desenhados à mão ----
    static readonly string[][] Digits =
    {
        new[] { ".##.", "#..#", "#..#", "#..#", "#..#", "#..#", "#..#", "#..#", ".##." }, // 0
        new[] { "..#.", ".##.", "#.#.", "..#.", "..#.", "..#.", "..#.", "..#.", ".###" }, // 1
        new[] { ".##.", "#..#", "...#", "...#", "..#.", ".#..", "#...", "#...", "####" }, // 2
        new[] { ".##.", "#..#", "...#", "...#", ".##.", "...#", "...#", "#..#", ".##." }, // 3
        new[] { "...#", "..##", ".#.#", "#..#", "#..#", "####", "...#", "...#", "...#" }, // 4
        new[] { "####", "#...", "#...", "###.", "...#", "...#", "...#", "#..#", ".##." }, // 5
        new[] { ".##.", "#..#", "#...", "#...", "###.", "#..#", "#..#", "#..#", ".##." }, // 6
        new[] { "####", "...#", "...#", "..#.", "..#.", ".#..", ".#..", ".#..", ".#.." }, // 7
        new[] { ".##.", "#..#", "#..#", "#..#", ".##.", "#..#", "#..#", "#..#", ".##." }, // 8
        new[] { ".##.", "#..#", "#..#", "#..#", ".###", "...#", "...#", "#..#", ".##." }, // 9
    };
    static readonly string[] CharF    = { "####", "#...", "#...", "#...", "###.", "#...", "#...", "#...", "#..." };
    static readonly string[] CharDash = { "....", "....", "....", "....", "####", "....", "....", "....", "...." };
    static readonly string[] MouseArt = { ".###.", "#.#.#", "#.#.#", "#####", "#...#", "#...#", "#...#", "#...#", ".###." };
    static readonly string[] HeadsetArt = { ".####.", "#....#", "#....#", "#....#", "##..##", "##..##", "##..##" };
    static readonly string[] KeyboardArt = { "######", "#.#.##", "##.#.#", "#.#.##", "######" };
    static readonly string[] OtherArt = { ".##.", "####", "#..#", "#..#", "####", "####", "####", "####" }; // bateria

    static void Blit(uint[] px, int sz, int scale, int ox, int oy, string[] art, uint rgb)
    {
        uint c = Premul(rgb, 255);
        for (int y = 0; y < art.Length; y++)
            for (int x = 0; x < art[y].Length; x++)
                if (art[y][x] == '#')
                    for (int yy = 0; yy < scale; yy++)
                        for (int xx = 0; xx < scale; xx++)
                        {
                            int X = ox + (x * scale) + xx, Y = oy + (y * scale) + yy;
                            if ((uint)X < (uint)sz && (uint)Y < (uint)sz) px[Y * sz + X] = c;
                        }
    }

    // Monta o ícone em BGRA pré-multiplicado (sz*sz).
    static uint[] Compose(string text, uint rgb, int glyph, uint glyphRgb, int sz)
    {
        var outPx = new uint[sz * sz];
        if (glyph == GlyphNone)
        {
            // Só o número, grande (fonte do sistema com antialiasing).
            byte[] cov = Fit(text, "Segoe UI", 700, sz, sz);
            for (int i = 0; i < cov.Length; i++) outPx[i] = Premul(rgb, cov[i]);
            return outPx;
        }

        // Grade lógica 16x16, ampliada por fator inteiro em telas com escala (fica sempre nítido).
        int k = Math.Max(1, sz / 16), o = (sz - 16 * k) / 2;

        // Símbolo: colunas 0..5, centralizado na vertical
        string[] art = glyph switch { GlyphMouse => MouseArt, GlyphHeadset => HeadsetArt, GlyphKeyboard => KeyboardArt, _ => OtherArt };
        int aw = art[0].Length;
        Blit(outPx, sz, k, o + (6 - aw) / 2 * k, o + (16 - art.Length) / 2 * k, art, glyphRgb);

        // Número: colunas 7..15 (dois dígitos 4x9 com 1px de espaço), linhas 3..11
        var chars = new System.Collections.Generic.List<string[]>(2);
        if (text == "100") chars.Add(CharF);
        else foreach (char ch in text)
        {
            if (ch >= '0' && ch <= '9') chars.Add(Digits[ch - '0']);
            else chars.Add(CharDash);
        }
        if (chars.Count == 1 && ReferenceEquals(chars[0], CharDash)) chars.Add(CharDash); // "--"
        int width = chars.Count * 5 - 1;
        int x0 = 7 + (9 - width + 1) / 2;
        for (int i = 0; i < chars.Count; i++)
            Blit(outPx, sz, k, o + (x0 + i * 5) * k, o + 4 * k, chars[i], rgb);
        return outPx;
    }

    static uint Premul(uint rgb, uint a)
    {
        uint r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        return (a << 24) | ((r * a / 255) << 16) | ((g * a / 255) << 8) | (b * a / 255);
    }

    public static nint MakeIcon(string text, uint rgb, int glyph, uint glyphRgb)
    {
        int sz = IconSize();
        uint[] src = Compose(text, rgb, glyph, glyphRgb, sz);

        BITMAPINFOHEADER bi = default;
        bi.biSize = 40; bi.biWidth = sz; bi.biHeight = -sz; bi.biPlanes = 1; bi.biBitCount = 32;
        void* bits;
        nint dc = CreateCompatibleDC(0);
        nint color = CreateDIBSection(dc, &bi, 0, &bits, 0, 0);
        fixed (uint* s = src) Buffer.MemoryCopy(s, bits, sz * sz * 4, sz * sz * 4);
        nint mask = CreateBitmap(sz, sz, 1, 1, null);
        ICONINFO ii = new() { fIcon = 1, hbmMask = mask, hbmColor = color };
        nint icon = CreateIconIndirect(&ii);
        DeleteObject(mask);
        DeleteObject(color);
        DeleteDC(dc);
        return icon;
    }

    // Só para o modo --test: tira de ícones em tamanho real (linha de cima) e ampliados 8x (linha de baixo).
    public static void DumpStrip(string path, (string text, uint rgb, int glyph)[] items)
    {
        int sz = IconSize(), S = 8, pad = 8;
        int cell = sz * S + pad;
        int Wd = items.Length * cell, Ht = sz * 2 + pad + sz * S;
        var img = new uint[Wd * Ht];
        Array.Fill(img, 0x202020u);
        for (int i = 0; i < items.Length; i++)
        {
            uint[] px = Compose(items[i].text, items[i].rgb, items[i].glyph, 0xFFFFFF, sz);
            for (int y = 0; y < sz; y++)
                for (int x = 0; x < sz; x++)
                {
                    uint c = Blend(px[y * sz + x]);
                    img[(y + 4) * Wd + i * cell + x + 4] = c;
                    for (int yy = 0; yy < S; yy++)
                        for (int xx = 0; xx < S; xx++)
                            img[(sz * 2 + pad + y * S + yy) * Wd + i * cell + x * S + xx] = c;
                }
        }
        int stride = Wd * 3 + 3 & ~3;
        var data = new byte[54 + stride * Ht];
        data[0] = (byte)'B'; data[1] = (byte)'M';
        BitConverter.TryWriteBytes(data.AsSpan(2), data.Length);
        BitConverter.TryWriteBytes(data.AsSpan(10), 54);
        BitConverter.TryWriteBytes(data.AsSpan(14), 40);
        BitConverter.TryWriteBytes(data.AsSpan(18), Wd);
        BitConverter.TryWriteBytes(data.AsSpan(22), -Ht);
        BitConverter.TryWriteBytes(data.AsSpan(26), (short)1);
        BitConverter.TryWriteBytes(data.AsSpan(28), (short)24);
        for (int y = 0; y < Ht; y++)
            for (int x = 0; x < Wd; x++)
            {
                uint c = img[y * Wd + x]; int o = 54 + y * stride + x * 3;
                data[o] = (byte)c; data[o + 1] = (byte)(c >> 8); data[o + 2] = (byte)(c >> 16);
            }
        File.WriteAllBytes(path, data);

        static uint Blend(uint p) // pré-multiplicado sobre fundo 0x20
        {
            uint a = p >> 24, inv = 255 - a, bg = 0x20 * inv / 255;
            return ((((p >> 16) & 0xFF) + bg) << 16) | ((((p >> 8) & 0xFF) + bg) << 8) | ((p & 0xFF) + bg);
        }
    }
}
