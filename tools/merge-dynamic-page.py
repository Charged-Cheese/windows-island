# -*- coding: utf-8 -*-
"""把剪贴板列表并入动态页，并删除独立的歌词页/剪贴板页。"""
import io
import sys

PATH = r"D:\代码区块\DeepSeek 工作区\DynamicIsland\src\DynamicIsland\MainWindow.xaml"

CLIP_BLOCK = '''                                    <ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto" Margin="0,0,0,4"
                                                  Style="{StaticResource HistoryScrollViewerStyle}"
                                                  HorizontalScrollBarVisibility="Disabled"
                                                  PreviewMouseWheel="ContentScroll_PreviewMouseWheel"
                                                  Thumb.DragStarted="HistoryScroll_DragStarted"
                                                  Thumb.DragCompleted="HistoryScroll_DragCompleted"
                                                  Visibility="{Binding ShowClipboardPanel, Converter={StaticResource BoolToVis}}">
                                        <ItemsControl ItemsSource="{Binding ClipboardItems}">
                                            <ItemsControl.ItemTemplate>
                                                <DataTemplate>
                                                    <Grid>
                                                        <Border CornerRadius="8" Padding="6,5" Background="Transparent"
                                                                Cursor="Hand" Tag="{Binding}" MouseLeftButtonUp="Clipboard_Click">
                                                            <Border.Style>
                                                                <Style TargetType="Border">
                                                                    <Style.Triggers>
                                                                        <Trigger Property="IsMouseOver" Value="True">
                                                                            <Setter Property="Background" Value="#10FFFFFF" />
                                                                        </Trigger>
                                                                    </Style.Triggers>
                                                                </Style>
                                                            </Border.Style>
                                                            <Grid>
                                                                <Grid.ColumnDefinitions>
                                                                    <ColumnDefinition Width="Auto" />
                                                                    <ColumnDefinition Width="*" />
                                                                    <ColumnDefinition Width="Auto" />
                                                                </Grid.ColumnDefinitions>
                                                                <Border Width="32" Height="24" CornerRadius="6"
                                                                        Background="{DynamicResource ControlBrush}"
                                                                        VerticalAlignment="Top" Margin="0,0,8,0"
                                                                        ClipToBounds="True">
                                                                    <Grid>
                                                                        <TextBlock Text="&#xE8C8;" FontFamily="{StaticResource IconFont}"
                                                                                   FontSize="10" Foreground="{DynamicResource AccentBrush}"
                                                                                   HorizontalAlignment="Center" VerticalAlignment="Center"
                                                                                   Visibility="{Binding IsText, Converter={StaticResource BoolToVis}}" />
                                                                        <Image Source="{Binding Thumbnail}" Stretch="UniformToFill"
                                                                               Visibility="{Binding IsImage, Converter={StaticResource BoolToVis}}" />
                                                                    </Grid>
                                                                </Border>
                                                                <StackPanel Grid.Column="1" VerticalAlignment="Center" ClipToBounds="True">
                                                                    <TextBlock Text="{Binding PreviewOneLine}" FontFamily="{StaticResource UiFont}"
                                                                               FontSize="10.5" Foreground="{DynamicResource PrimaryTextBrush}"
                                                                               TextTrimming="CharacterEllipsis" />
                                                                    <TextBlock Text="{Binding Meta}" FontFamily="{StaticResource UiFont}"
                                                                               FontSize="8.5" Foreground="{DynamicResource TertiaryTextBrush}"
                                                                               TextTrimming="CharacterEllipsis" Margin="0,1,0,0" />
                                                                </StackPanel>
                                                                <TextBlock Grid.Column="2" Text="{Binding TimeText}"
                                                                           FontFamily="{StaticResource UiFont}" FontSize="8.5"
                                                                           Foreground="{DynamicResource TertiaryTextBrush}"
                                                                           VerticalAlignment="Top" Margin="0,3,0,0" />
                                                            </Grid>
                                                        </Border>
                                                        <Border Height="1" Background="{DynamicResource DividerBrush}"
                                                                Opacity="0.5" VerticalAlignment="Bottom" />
                                                    </Grid>
                                                </DataTemplate>
                                            </ItemsControl.ItemTemplate>
                                        </ItemsControl>
                                    </ScrollViewer>'''.split("\n")


def main() -> int:
    with io.open(PATH, encoding="utf-8") as handle:
        lines = handle.read().split("\n")

    def find(predicate, start=0):
        for index in range(start, len(lines)):
            if predicate(lines[index]):
                return index
        return -1

    lyrics = find(lambda line: "<!-- 歌词页" in line)
    calendar = find(lambda line: "<!-- 日历页：独立占满整个面板" in line)
    if lyrics < 0 or calendar < 0 or calendar < lyrics:
        print("锚点未找到：lyrics=%d calendar=%d" % (lyrics, calendar))
        return 1

    # 通知页（现动态页）的结束 </Grid> 就在歌词注释之上
    close = lyrics - 1
    while close >= 0 and lines[close].strip() != "</Grid>":
        close -= 1
    if close < 0:
        print("找不到通知页结束标签")
        return 1

    lines[close:close] = CLIP_BLOCK

    # 删除独立的歌词页与剪贴板页（从歌词注释到日历注释前的空行）
    lyrics = find(lambda line: "<!-- 歌词页" in line)
    calendar = find(lambda line: "<!-- 日历页：独立占满整个面板" in line, lyrics)
    if lyrics < 0 or calendar < 0:
        print("删除时锚点丢失：lyrics=%d calendar=%d" % (lyrics, calendar))
        return 1

    del lines[lyrics:calendar]

    with io.open(PATH, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines))

    print("OK：插入剪贴板块 %d 行，删除独立页 %d 行" % (len(CLIP_BLOCK), calendar - lyrics))
    return 0


if __name__ == "__main__":
    sys.exit(main())
