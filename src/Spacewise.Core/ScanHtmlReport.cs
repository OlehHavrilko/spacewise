using System.Globalization;
using System.Net;
using System.Text;

namespace Spacewise.Core;

public static class ScanHtmlReport
{
    public static string Build(ScanResult scan, DateTime utc, bool includePaths = false)
    {
        static string H(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Spacewise — анализ диска</title><style>body{font:16px system-ui;max-width:1100px;margin:40px auto;padding:0 20px;color:#172a3a}table{border-collapse:collapse;width:100%;margin:20px 0}th,td{padding:9px;border-bottom:1px solid #dbe2e8;text-align:left;overflow-wrap:anywhere}small{color:#526574}svg{width:100%;height:240px}h1,h2{color:#1b607f}@media print{body{margin:0}tr{break-inside:avoid}}</style><h1>Spacewise — анализ диска</h1>");
        html.Append("<p>").Append(H(utc.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture))).Append(" · ").Append(H(scan.State.ToString())).Append("</p>");
        if (includePaths) html.Append("<p>Корень: ").Append(H(scan.Root)).Append("</p>");
        html.Append("<p>Файлов: ").Append(scan.Files.ToString(CultureInfo.InvariantCulture)).Append("; логический размер: ")
            .Append(H(DisplayFormat.Size(scan.LogicalBytes))).Append("; пропущено: ").Append(scan.Skipped).Append("; ошибок: ").Append(scan.Errors).Append(".</p>");
        if (scan.State != ScanState.Completed || scan.StoppedEarly || scan.FolderAggregationTruncated)
            html.Append("<p><strong>Результат неполный или агрегаты папок ограничены.</strong></p>");
        html.Append("<small>Логический размер не равен освобождаемому месту. Вложенные итоги папок нельзя складывать. Пути: ")
            .Append(includePaths ? "включены" : "скрыты").Append(".</small>");
        FolderAggregate[] roots = scan.Folders.Where(f => Path.GetDirectoryName(f.Path) == Path.TrimEndingDirectorySeparator(scan.Root)).Take(30).ToArray();
        var boxes = TreemapLayout.Arrange(roots.Select(f => (double)f.LogicalBytes).ToArray(), 1000, 240);
        html.Append("<h2>Карта папок</h2><svg viewBox=\"0 0 1000 240\" role=\"img\" aria-label=\"Размеры папок\">");
        for (int i = 0; i < boxes.Count; i++)
        {
            MapRectangle box = boxes[i];
            string label = includePaths ? roots[i].Path : "Папка " + (i + 1);
            html.Append(CultureInfo.InvariantCulture, $"<rect x=\"{box.X:F3}\" y=\"{box.Y:F3}\" width=\"{box.Width:F3}\" height=\"{box.Height:F3}\" fill=\"hsl({190 + i * 19 % 100} 45% 40%)\" stroke=\"white\"><title>{H(label)}: {H(DisplayFormat.Size(roots[i].LogicalBytes))}</title></rect>");
        }
        html.Append("</svg><h2>Папки</h2><table><tr><th>Папка</th><th>Файлов</th><th>Логический размер</th></tr>");
        int row = 0;
        foreach (FolderAggregate folder in scan.Folders.Take(100))
            html.Append("<tr><td>").Append(H(includePaths ? folder.Path : "Папка " + ++row)).Append("</td><td>").Append(folder.Files)
                .Append("</td><td>").Append(H(DisplayFormat.Size(folder.LogicalBytes))).Append("</td></tr>");
        html.Append("</table><h2>Крупнейшие файлы</h2><table><tr><th>Файл</th><th>Логический размер</th><th>На диске</th></tr>");
        row = 0;
        foreach (FileFinding file in scan.LargestFiles)
            html.Append("<tr><td>").Append(H(includePaths ? file.Path : "Файл " + ++row)).Append("</td><td>").Append(H(DisplayFormat.Size(file.LogicalBytes)))
                .Append("</td><td>").Append(file.AllocatedBytes is long allocated ? H(DisplayFormat.Size(allocated)) : "неизвестно").Append("</td></tr>");
        return html.Append("</table></html>").ToString();
    }
}
