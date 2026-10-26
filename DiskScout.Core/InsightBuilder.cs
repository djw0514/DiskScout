namespace DiskScout.Core;

public static class InsightBuilder
{
    public static IReadOnlyList<ScanInsight> Build(ScanResult result)
    {
        var insights = new List<ScanInsight>();

        if (result.Folders.FirstOrDefault() is { } largest && result.TotalBytes > 0)
        {
            var share = largest.Size * 100d / result.TotalBytes;
            insights.Add(new ScanInsight(
                "最大目录值得优先查看",
                $"“{largest.Name}”占本次可见文件的 {share:0.#}%（{largest.SizeText}）。先确认它是否仍在使用，再决定迁移或清理。",
                share >= 40 ? InsightLevel.Warning : InsightLevel.Attention));
        }

        var oldLargeFiles = result.LargeFiles
            .Where(x => x.LastWriteTime < DateTime.Now.AddYears(-1))
            .ToArray();
        if (oldLargeFiles.Length > 0)
        {
            var total = oldLargeFiles.Sum(x => x.Size);
            insights.Add(new ScanInsight(
                "发现长期未修改的大文件",
                $"{oldLargeFiles.Length:N0} 个大文件超过一年未修改，共 {ByteSizeFormatter.Format(total)}。适合检查旧安装包、镜像、备份和视频素材。",
                InsightLevel.Attention));
        }

        var archiveSize = result.Categories
            .Where(x => x.Name is "压缩包/镜像" or "数据与虚拟磁盘")
            .Sum(x => x.Size);
        if (archiveSize >= 1024L * 1024 * 1024)
        {
            insights.Add(new ScanInsight(
                "归档或磁盘镜像占用较高",
                $"压缩包、镜像、备份与虚拟磁盘合计 {ByteSizeFormatter.Format(archiveSize)}，通常可迁移到普通盘或外置存储。",
                InsightLevel.Attention));
        }

        if (result.Issues.Count > 0)
        {
            insights.Add(new ScanInsight(
                "部分目录未能读取",
                $"记录到 {result.Issues.Count:N0} 个访问问题，当前统计可能小于实际占用。扫描系统盘时可尝试“管理员重启”。",
                InsightLevel.Info));
        }

        if (insights.Count == 0)
        {
            insights.Add(new ScanInsight(
                "扫描完成，未发现明显异常",
                "可从“目录排行”和“大文件”按占用从高到低逐项确认；不要直接删除不熟悉的系统目录。",
                InsightLevel.Info));
        }

        insights.Add(new ScanInsight(
            "统计口径说明",
            "这里统计文件逻辑大小；压缩文件、稀疏文件、硬链接、系统还原点及文件系统元数据会让结果与磁盘已用空间存在差异。",
            InsightLevel.Info));

        return insights;
    }
}
