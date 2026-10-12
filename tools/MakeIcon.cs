// Рисует значок Replay Overlay и сохраняет многоразмерный .ico (PNG внутри).
// Сборка и запуск: см. build.ps1 (-Icon). Результат: assets/icon.ico и assets/icon.png.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon {
 static Bitmap Draw(int size) {
  var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
  using (var g = Graphics.FromImage(bmp)) {
   g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
   float s = size / 256f;
   using (var path = Rounded(new RectangleF(8 * s, 8 * s, 240 * s, 240 * s), 56 * s))
   using (var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(0, size), Color.FromArgb(255, 28, 33, 38), Color.FromArgb(255, 14, 16, 19))) {
    g.FillPath(bg, path);
    using (var border = new Pen(Color.FromArgb(255, 57, 67, 47), Math.Max(1f, 4 * s))) g.DrawPath(border, path);
   }
   var green = Color.FromArgb(255, 141, 204, 56);
   // Круговая стрелка «повтор»: дуга почти по кругу и наконечник в её начале.
   const float start = -40, sweep = 270, R = 70;
   using (var pen = new Pen(green, 22 * s) { StartCap = LineCap.Round, EndCap = LineCap.Flat }) g.DrawArc(pen, (128 - R) * s, (128 - R) * s, 2 * R * s, 2 * R * s, start, sweep);
   // Наконечник в конце дуги: остриё вдоль касательной (по часовой), основание поперёк дуги.
   double a = (start + sweep) * Math.PI / 180; float ux = (float)Math.Cos(a), uy = (float)Math.Sin(a), tx = -uy, ty = ux;
   float px = 128 * s + ux * R * s, py = 128 * s + uy * R * s;
   var tip = new[] { new PointF(px + tx * 34 * s, py + ty * 34 * s), new PointF(px + ux * 26 * s, py + uy * 26 * s), new PointF(px - ux * 26 * s, py - uy * 26 * s) };
   using (var b = new SolidBrush(green)) {
    g.FillPolygon(b, tip);
    g.FillPolygon(b, new[] { new PointF(112 * s, 98 * s), new PointF(112 * s, 158 * s), new PointF(162 * s, 128 * s) }); // «воспроизвести»
   }
  }
  return bmp;
 }
 static GraphicsPath Rounded(RectangleF r, float radius) {
  var p = new GraphicsPath(); float d = radius * 2;
  p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
  p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
  return p;
 }
 static void Main(string[] args) {
  string outDir = args.Length > 0 ? args[0] : ".";
  int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
  var images = new List<byte[]>();
  foreach (int size in sizes) using (var bmp = Draw(size)) using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); images.Add(ms.ToArray()); }
  using (var big = Draw(512)) big.Save(Path.Combine(outDir, "icon.png"), ImageFormat.Png);
  using (var fs = File.Create(Path.Combine(outDir, "icon.ico"))) using (var w = new BinaryWriter(fs)) {
   w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
   int offset = 6 + 16 * sizes.Length;
   for (int i = 0; i < sizes.Length; i++) {
    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
    w.Write(images[i].Length); w.Write(offset); offset += images[i].Length;
   }
   foreach (var img in images) w.Write(img);
  }
  Console.WriteLine("icon.ico, icon.png -> " + Path.GetFullPath(outDir));
 }
}
