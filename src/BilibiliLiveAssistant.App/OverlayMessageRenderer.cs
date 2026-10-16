using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

/// <summary>All overlay regions share one cached alpha renderer, including interactions and room metrics.</summary>
internal static class OverlayMessageRenderer
{
    // Split only at grapheme or complete emote boundaries. Queued messages have no bitmaps.
    public static IReadOnlyList<LiveEvent> ScrollParts(LiveEvent value, OverlayRegionSettings region, DashboardSettings style, BiliImageCache cache)
    {
        if (value.Extra.ContainsKey("room_bulletin")) return new[] { value };
        if (value.Appearance?.FullEmote is not null) return new[] { value };
        var metadata = new Dictionary<string, object?>(value.Extra) { ["scroll_part"] = "999/999", ["scroll_price"] = Price(value) };
        using var header = Render(value with { Content = "", Metadata = metadata }, region, style, cache,
            maxHeight: Math.Max(region.FontSize * 2 + 12, region.EmoteSize + 8));
        var budget = Math.Clamp(4096 - header.Width - 128, 128, 1500);
        using var image = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(image);
        using var font = OverlayTextRenderer.CreateFont(region.FontFamily, region.FontSize, region.Bold, region.Italic);
        var parts = new List<string>(); var text = new StringBuilder(); float used = 0;
        var widths = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var segment in RichMessageSegments.Split(Content(value), value.Appearance))
        {
            if (segment.Emote is { } emote)
                Add(segment.Text, region.EmoteSize * Math.Max(1f, emote.Width) / Math.Max(1, emote.Height) + 4);
            else
            {
                var elements = StringInfo.GetTextElementEnumerator(segment.Text);
                while (elements.MoveNext())
                {
                    var atom = elements.GetTextElement().Replace('\r', ' ').Replace('\n', ' ');
                    if (!widths.TryGetValue(atom, out var width)) widths[atom] = width = OverlayTextRenderer.Measure(graphics, atom, font).Width + 1;
                    Add(atom, width);
                }
            }
        }
        if (text.Length > 0 || parts.Count == 0) parts.Add(text.ToString());
        return parts.Select((part, index) => value with { Content = part,
            Metadata = new Dictionary<string, object?>(value.Extra) { ["scroll_part"] = parts.Count > 1 ? $"{index + 1}/{parts.Count}" : "", ["scroll_price"] = Price(value) } }).ToArray();
        void Add(string atom, float width)
        {
            if (text.Length > 0 && (used + width > budget || text.Length + atom.Length > 512))
            { parts.Add(text.ToString()); text.Clear(); used = 0; }
            text.Append(atom); used += width;
        }
    }
    internal static int ImageDecodeLimit(LiveEvent value, string url, DashboardSettings style, OverlayRegionSettings region)
    {
        if (value.Extra.GetValueOrDefault("room_bulletin") is LiveRoomBulletin bulletin)
        {
            foreach (var member in bulletin.Members.Take(10))
                if (member.Appearance?.AvatarUrl == url)
                    return ImageDecodeLimit(OverlayBulletinRenderer.MemberEvent(member, region.Color), url, style, region);
        }
        // RegionSettings has already been scaled by ForRendering. Overlay
        // coordinates are physical pixels, unlike WinUI's DPI-scaled DIPs.
        var required = 0d;
        if (value.Appearance is { } appearance)
        {
            // The banner is sized from its rendered text, so keep its full tier.
            // These decorations also use their decoded aspect ratio for layout.
            if (appearance.EntryEffect?.BackgroundUrl == url || appearance.InteractionIconUrl == url || appearance.EntryEffect?.BadgeUrl == url) return 256;
            if (appearance.AvatarUrl == url) required = style.AvatarSize * region.ContentScale;
            if (appearance.AvatarFrameUrl == url) required = Math.Max(required, style.AvatarSize * region.ContentScale + 6);
            if (appearance.FullEmote is { } full && full.Url == url) IncludeEmote(full, region.LargeEmoteHeight);
            foreach (var emote in appearance.Emotes)
                if (emote.Url == url) IncludeEmote(emote, appearance.FullEmote is not null ? region.LargeEmoteHeight : region.EmoteSize);
        }
        if (value.HistoryFields?.GiftIconUrl == url || value.Extra.GetValueOrDefault("gift_icon") as string == url) required = Math.Max(required, 28);
        return required > 0 ? BiliImageCache.SizeTier(Math.Ceiling(required)) : 256;
        void IncludeEmote(LiveEmote emote, int height)
        {
            // Layout uses this same metadata ratio and can only shrink the image
            // further to fit the row/card. A wide emote needs both dimensions.
            var width = height * Math.Max(1d, emote.Width) / Math.Max(1, emote.Height);
            required = Math.Max(required, Math.Max(width, height));
        }
    }

    internal static BiliImageRole ImageRole(LiveEvent value, string url)
    {
        if (value.Extra.GetValueOrDefault("room_bulletin") is LiveRoomBulletin bulletin)
            foreach (var member in bulletin.Members.Take(10))
                if (member.Appearance?.AvatarUrl == url) return BiliImageRole.Avatar;
        return value.Appearance is { } appearance && appearance.AvatarUrl == url && appearance.FullEmote?.Url != url
            && appearance.EntryEffect?.BackgroundUrl != url && !appearance.Emotes.Any(e => e.Url == url)
            ? BiliImageRole.Avatar : BiliImageRole.Content;
    }

    internal static async Task<LiveEvent> ResolveAvatarAsync(LiveEvent value, DashboardSettings style, BiliImageCache cache, CancellationToken cancellation)
    {
        if (!style.ShowsAvatar(value) || MessageCard.AvatarUid(value) <= 0 || BiliImageCache.NormalizeUrl(value.Appearance?.AvatarUrl) is not null) return value;
        var avatar = await cache.Avatars.ResolveAsync(value.Uid, value.Appearance?.AvatarUrl, cancellation);
        return avatar.Length == 0 ? value : value with { Appearance = (value.Appearance ?? new()) with { AvatarUrl = avatar } };
    }

    public static IEnumerable<string> ImageUrls(LiveEvent value, DashboardSettings style)
    {
        if (value.HistoryFields?.GiftIconUrl is { Length: > 0 } giftIcon) yield return giftIcon;
        else if (value.Extra.GetValueOrDefault("gift_icon") is string { Length: > 0 } rawIcon) yield return rawIcon;
        if (value.Extra.TryGetValue("room_bulletin", out var data) && data is LiveRoomBulletin bulletin)
        {
            foreach (var member in bulletin.Members.Take(10))
                foreach (var url in ImageUrls(OverlayBulletinRenderer.MemberEvent(member, value.Color), style)) yield return url;
            yield break;
        }
        if (style.ShowsAvatar(value) && value.Appearance?.AvatarUrl is { Length: > 0 } avatar) yield return avatar;
        if (style.ShowsAvatar(value) && style.ShowIdentity && value.Appearance?.AvatarFrameUrl is { Length: > 0 } frame) yield return frame;
        if (style.ShowIdentity && value.Appearance?.InteractionIconUrl is { Length: > 0 } icon) yield return icon;
        if (style.ShowIdentity && value.Appearance?.EntryEffect?.BadgeUrl is { Length: > 0 } badge) yield return badge;
        if (value.Appearance?.EntryEffect?.BackgroundUrl is { Length: > 0 } background) yield return background;
        if (value.Appearance?.FullEmote is { } full) yield return full.Url;
        if (value.Appearance is { } appearance)
            foreach (var emote in appearance.Emotes)
                if (!string.IsNullOrEmpty(emote.Token) && value.Content.Contains(emote.Token, StringComparison.Ordinal)) yield return emote.Url;
    }

    public static Bitmap Render(LiveEvent value, OverlayRegionSettings region, DashboardSettings style, BiliImageCache cache,
        int width = 0, int maxHeight = 128, bool card = false, bool background = true, bool inlineHeader = false)
    {
        using var timing = PerformanceCounters.Measure(2);
        var imageStyle = style;
        if (!card && value.Extra.TryGetValue("room_bulletin", out var data) && data is LiveRoomBulletin bulletin)
            return OverlayBulletinRenderer.Render(bulletin, background ? region : region with { BackgroundOpacity = 0 }, style, cache, maxHeight);
        style = style with { AvatarSize = (int)Math.Round(style.AvatarSize * region.ContentScale) };
        var showAvatar = style.ShowsAvatar(value);
        var rankMember = value.Extra.TryGetValue("rank_position", out var rankPosition);
        var entry = value.Type == LiveEventType.Enter && !string.IsNullOrEmpty(value.Username);
        var follow = LiveInteractions.IsFollow(value);
        var like = LiveInteractions.IsLike(value);
        if (entry) card = false;
        maxHeight = Math.Max(36, maxHeight);
        var maxWidth = Math.Clamp(width > 0 ? width : rankMember ? 3000 : 4096, 24, 7680);
        var pad = Math.Max(card ? (int)(Math.Clamp(style.Padding, 10, 24) * region.ContentScale) : 4, region.Shadow ? region.ShadowOffset + region.ShadowSize + 2 : 4);
        var system = value.Type is LiveEventType.Online or LiveEventType.Rank || string.IsNullOrEmpty(value.Username);
        var accent = OverlayTextRenderer.Color(follow ? "#FF8AB3" : value.Type == LiveEventType.Rank ? "#B998FF" : region.Color);
        using var bodyFont = OverlayTextRenderer.CreateFont(region.FontFamily, region.FontSize, region.Bold || follow, region.Italic);
        using var nameFont = OverlayTextRenderer.CreateFont(region.FontFamily, Math.Max(12, region.FontSize * (entry || rankMember ? 1 : 0.8f)), true);
        using var badgeFont = OverlayTextRenderer.CreateFont(region.FontFamily, Math.Max(8, Math.Min(15 * (float)region.ContentScale, region.FontSize * 0.5f)), true);
        using var measureImage = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(measureImage);
        var framedAvatar = showAvatar && style.ShowIdentity && value.Appearance?.AvatarFrameUrl is { Length: > 0 };
        var rowHeight = Math.Max(bodyFont.GetHeight(measure) + Math.Clamp(style.LineSpacing, 3, 10), showAvatar && !system ? Math.Min(style.AvatarSize, region.FontSize + 10) + (framedAvatar ? 6 : 0) : 0);
        var hasInlineEmotes = value.Appearance is { FullEmote: null } appearance && appearance.Emotes.Any(emote =>
            !string.IsNullOrEmpty(emote.Token) && value.Content.Contains(emote.Token, StringComparison.Ordinal));
        if (hasInlineEmotes) rowHeight = Math.Max(rowHeight, region.EmoteSize);
        if (!card && value.Appearance?.FullEmote is not null) rowHeight = Math.Max(rowHeight, region.LargeEmoteHeight);
        var headerHeight = card ? Math.Max(nameFont.GetHeight(measure) + 8, showAvatar && !system ? Math.Min(style.AvatarSize, region.FontSize + 12) : 0) : rowHeight;
        var pieces = new List<Piece>();
        var images = new Dictionary<string, Bitmap?>();
        var x = (float)pad; var y = (float)pad;
        var startX = (float)pad;
        var right = maxWidth - pad;
        var lineHeight = card ? headerHeight : rowHeight;
        var bottom = y + lineHeight;
        var furthest = x;
        var stop = false;
        try
        {
            if (showAvatar && !system)
            {
                var size = Math.Max(16, Math.Min(style.AvatarSize, (int)headerHeight - (framedAvatar ? 6 : 0)));
                var bounds = new RectangleF(x + (framedAvatar ? 3 : 0), y + (headerHeight - size) / 2, size, size);
                pieces.Add(new Piece { Image = Load(value.Appearance?.AvatarUrl ?? ""), Bounds = bounds, Avatar = true });
                if (framedAvatar && Load(value.Appearance!.AvatarFrameUrl) is { } frame)
                    pieces.Add(new Piece { Image = frame, Bounds = new(bounds.X - 3, bounds.Y - 3, size + 6, size + 6) });
                x += size + (framedAvatar ? 6 : 0) + (rankMember ? 4 : 8);
            }
            startX = x;
            if (follow) Badge(LiveInteractions.FollowLabel(value), OverlayTextRenderer.Color("#AD3E6C"));
            if (like) Badge("点赞", OverlayTextRenderer.Color("#A75432"));
            if (rankMember) Badge($"#{rankPosition}", accent);
            if (card && !inlineHeader || value.Type is LiveEventType.Gift or LiveEventType.Guard or LiveEventType.SuperChat) Badge(Label(value), accent);
            if (entry && style.ShowIdentity && value.Appearance is { GuardLevel: 0, WealthLevel: > 0, EntryEffect: { } entryEffect } wealth)
            {
                if (!ImageBadge(entryEffect.BadgeUrl)) Badge($"◇ {wealth.WealthLevel}", OverlayTextRenderer.Color(LiveEntryStyles.Accent(wealth)));
            }
            if (style.ShowMedals && value.Appearance is { MedalName.Length: > 0, MedalLevel: > 0 } medal)
                Badge($"{medal.MedalName} {medal.MedalLevel}", OverlayTextRenderer.Color(medal.MedalColor));
            if (style.ShowIdentity && value.Appearance is { } identity)
            {
                var guard = identity.GuardLevel switch { 1 => "总督", 2 => "提督", 3 => "舰长", _ => "" };
                if (guard.Length > 0) Badge(guard, OverlayTextRenderer.Color("#458AC1"));
                if (identity.IsAdmin) Badge("房管", OverlayTextRenderer.Color("#358867"));
            }
            if (!system)
                Text((value.Username.Length > 48 ? value.Username[..48] + "…" : value.Username) + " ", nameFont,
                    OverlayTextRenderer.Color(follow ? "#FFE4EF" : entry ? LiveEntryStyles.UsernameColor(value.Appearance, style.UsernameColor) : style.UsernameColor, style.TextOpacity), true);
            if (card && !system && !inlineHeader) { NewLine(); lineHeight = rowHeight; bottom = y + lineHeight; }
            else if (card) { if (!inlineHeader) startX = x; lineHeight = Math.Max(headerHeight, rowHeight); bottom = y + lineHeight; }
            if (card && value.Type is LiveEventType.Gift or LiveEventType.Guard)
            {
                var size = Math.Min(28, rowHeight); var iconUrl = value.HistoryFields?.GiftIconUrl ?? value.Extra.GetValueOrDefault("gift_icon") as string ?? "";
                pieces.Add(new Piece { Image = Load(iconUrl) ?? MakeGiftIcon(value.Type == LiveEventType.Guard), Bounds = new(x, y + (rowHeight - size) / 2, size, size) });
                x += size + 6;
            }
            stop = false;
            var content = Content(value);
            if (!card) content = content.Replace('\r', ' ').Replace('\n', ' ');
            var limit = card ? 2048 : value.Type == LiveEventType.Danmaku ? 256 : 2048;
            if (content.Length > limit) content = content[..limit] + "…";
            foreach (var segment in RichMessageSegments.Split(content, value.Appearance))
            {
                if (stop) break;
                if (segment.Emote is not { } emote) { Text(segment.Text, bodyFont, OverlayTextRenderer.Color(follow ? "#FFD275" : like ? "#FFBF98" : entry ? value.Appearance?.EntryEffect?.TextColor ?? "#B2BDCD" : card ? style.TextColor : region.Color, style.TextOpacity)); continue; }
                var large = value.Appearance?.FullEmote is not null;
                var aspect = Math.Max(1f, emote.Width) / Math.Max(1, emote.Height);
                var h = Math.Min(large ? region.LargeEmoteHeight : region.EmoteSize, Math.Max(0, maxHeight - y - pad));
                // Fit both dimensions proportionally; a narrow card must never
                // stretch a wide emote or reserve more than its available width.
                h = Math.Min(h, Math.Max(0, right - startX) / aspect);
                var w = h * aspect;
                if (card && x + w > right) NewLine();
                h = Math.Min(h, Math.Max(0, maxHeight - y - pad)); w = h * aspect;
                if (h < 16 || !card && x + w > right) { Ellipsis(bodyFont); break; }
                var imageY = y + Math.Max(0, (lineHeight - h) / 2);
                if (imageY + h > maxHeight - pad) imageY = y;
                var bitmap = Load(emote.Url);
                pieces.Add(new Piece { Image = bitmap, Text = bitmap is null ? segment.Text : "", Font = bodyFont, Color = OverlayTextRenderer.Color(card ? style.TextColor : region.Color), Bounds = new(x, imageY, w, h), Clip = bitmap is null });
                x += w + 4; furthest = Math.Max(furthest, x); bottom = Math.Max(bottom, imageY + h);
            }
            if (entry && !stop && style.ShowIdentity && value.Appearance is { } relation)
            {
                if (!ImageBadge(relation.InteractionIconUrl) && relation.InteractionLabel.Length > 0)
                { x += 5; Badge(relation.InteractionLabel, OverlayTextRenderer.Color("#515C6B")); }
            }
            var height = Math.Clamp((int)Math.Ceiling(bottom) + pad, 36, maxHeight);
            var imageWidth = width > 0 ? maxWidth : Math.Clamp((int)Math.Ceiling(furthest) + pad + 2, 24, 4096);
            var sprite = new Bitmap(imageWidth, height, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(sprite);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            if (background) DrawBackground(graphics, sprite.Size, region, accent);
            if (follow)
            {
                using var rail = new Pen(accent, 3);
                graphics.DrawLine(rail, 2, 5, 2, height - 5);
            }
            if (entry && value.Appearance?.EntryEffect is { } effect && imageWidth > startX + pad + 1)
            {
                var bounds = new RectangleF(startX - 3, pad, imageWidth - startX - pad + 3, height - pad * 2);
                using var shape = OverlayTextRenderer.RoundedRectangle(bounds, bounds.Height / 2);
                var color = OverlayTextRenderer.Color(LiveEntryStyles.Accent(value.Appearance));
                using var fill = new LinearGradientBrush(bounds, Color.FromArgb(225, color), Color.FromArgb(0, color), LinearGradientMode.Horizontal);
                graphics.FillPath(fill, shape);
                if (Load(effect.BackgroundUrl) is { } entryBackground)
                {
                    var saved = graphics.Save(); graphics.SetClip(shape, CombineMode.Intersect);
                    graphics.DrawImage(entryBackground, bounds); graphics.Restore(saved);
                }
            }
            foreach (var piece in pieces)
            {
                if (piece.Avatar)
                {
                    var state = graphics.Save();
                    using var circle = new GraphicsPath(); circle.AddEllipse(piece.Bounds); graphics.SetClip(circle, CombineMode.Intersect);
                    if (piece.Image is not null) graphics.DrawImage(piece.Image, piece.Bounds);
                    else
                    {
                        using var fill = new SolidBrush(Color.FromArgb(255, 75, 91, 116)); graphics.FillEllipse(fill, piece.Bounds);
                        using var head = new SolidBrush(Color.FromArgb(255, 210, 220, 237));
                        graphics.FillEllipse(head, piece.Bounds.X + piece.Bounds.Width * 0.34f, piece.Bounds.Y + piece.Bounds.Height * 0.18f, piece.Bounds.Width * 0.32f, piece.Bounds.Height * 0.32f);
                        graphics.FillEllipse(head, piece.Bounds.X + piece.Bounds.Width * 0.18f, piece.Bounds.Y + piece.Bounds.Height * 0.58f, piece.Bounds.Width * 0.64f, piece.Bounds.Height * 0.5f);
                    }
                    graphics.Restore(state); continue;
                }
                if (piece.Badge)
                {
                    using var pill = OverlayTextRenderer.RoundedRectangle(piece.Bounds, 4);
                    using var fill = new SolidBrush(piece.Color); graphics.FillPath(fill, pill);
                    OverlayTextRenderer.Draw(graphics, piece.Text, badgeFont, new(piece.Bounds.X + 5, piece.Bounds.Y + 3), Color.White, false, region);
                    continue;
                }
                if (piece.Image is not null) { graphics.DrawImage(piece.Image, piece.Bounds); continue; }
                var saved = graphics.Save();
                if (piece.Clip) graphics.SetClip(piece.Bounds, CombineMode.Intersect);
                OverlayTextRenderer.Draw(graphics, piece.Text, piece.Font!, piece.Bounds.Location, piece.Color, region.Outline, region);
                graphics.Restore(saved);
            }
            return sprite;
        }
        finally { foreach (var image in images.Values) image?.Dispose(); }

        Bitmap? Load(string url)
        {
            if (images.TryGetValue(url, out var found)) return found;
            images[url] = null;
            if (cache.TryGetStaticFrame(url, ImageDecodeLimit(value, url, imageStyle, region), ImageRole(value, url)) is not { } frame) return null;
            try { return images[url] = frame.CreateDrawingBitmap(); }
            catch (ArgumentException) { return null; }
        }
        Bitmap MakeGiftIcon(bool guard)
        {
            var key = guard ? "local:guard" : "local:gift";
            if (images.TryGetValue(key, out var existing) && existing is not null) return existing;
            var icon = new Bitmap(32, 32, PixelFormat.Format32bppPArgb); images[key] = icon;
            using var g = Graphics.FromImage(icon); g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(accent, 2.4f) { LineJoin = LineJoin.Round };
            if (guard) { g.DrawPolygon(pen, [new PointF(4, 8), new(11, 14), new(16, 5), new(21, 14), new(28, 8), new(25, 26), new(7, 26)]); g.DrawLine(pen, 8, 21, 25, 21); }
            else { g.DrawRectangle(pen, 5, 13, 22, 15); g.DrawRectangle(pen, 3, 9, 26, 5); g.DrawLine(pen, 16, 9, 16, 28); g.DrawEllipse(pen, 7, 2, 9, 8); g.DrawEllipse(pen, 16, 2, 9, 8); }
            return icon;
        }
        void NewLine() { x = startX; y = bottom + (card ? 3 : 0); bottom = y + lineHeight; }
        bool ImageBadge(string url)
        {
            if (url.Length == 0 || Load(url) is not { } icon) return false;
            var h = Math.Min(rowHeight - 4, 24);
            var w = Math.Clamp(h * icon.Width / Math.Max(1f, icon.Height), h, 90);
            if (x + w + 5 <= right)
            {
                pieces.Add(new Piece { Image = icon, Bounds = new(x, y + (rowHeight - h) / 2, w, h) });
                x += w + 5; furthest = Math.Max(furthest, x);
            }
            return true;
        }
        void Badge(string text, Color color)
        {
            var badgeHeight = badgeFont.GetHeight(measure) + 6;
            var available = right - startX - 10;
            if (available <= 8) return;
            text = Fit(text, badgeFont, available, true);
            if (text.Length == 0) return;
            var badgeWidth = OverlayTextRenderer.Measure(measure, text, badgeFont).Width + 10;
            if (x + badgeWidth > right)
            {
                if (!card || y + headerHeight * 2 + rowHeight > maxHeight - pad) return;
                NewLine();
            }
            pieces.Add(new Piece { Text = text, Bounds = new(x, y + (lineHeight - badgeHeight) / 2, badgeWidth, badgeHeight), Badge = true, Color = color });
            x += badgeWidth + (rankMember ? 3 : 5); furthest = Math.Max(furthest, x);
        }
        void Text(string remaining, Font font, Color color, bool header = false)
        {
            while (remaining.Length > 0 && !stop)
            {
                if (remaining[0] is '\r' or '\n') { if (remaining[0] == '\n') NewLine(); remaining = remaining[1..]; continue; }
                var line = remaining.Split('\n', 2)[0].TrimEnd('\r');
                if (line.Length == 0) { remaining = remaining[1..]; continue; }
                if (y + font.GetHeight(measure) > maxHeight - pad) { Ellipsis(font); return; }
                var lastLine = !card || y + lineHeight * 2 + 3 > maxHeight - pad;
                var part = Fit(line, font, right - x, lastLine);
                if (part.Length == 0)
                {
                    if (card && !lastLine && x > startX) { NewLine(); continue; }
                    Ellipsis(font); return;
                }
                var size = OverlayTextRenderer.Measure(measure, part, font);
                var textY = y + Math.Max(0, (lineHeight - font.GetHeight(measure)) / 2);
                pieces.Add(new Piece { Text = part, Font = font, Bounds = new(x, textY, size.Width + 3, font.GetHeight(measure) + 3), Color = color });
                x += size.Width; furthest = Math.Max(furthest, x); bottom = Math.Max(bottom, textY + font.GetHeight(measure));
                if (part.EndsWith('…') && part != line) { stop = true; return; }
                remaining = remaining[part.Length..];
                if (part.Length < line.Length) NewLine();
                if (!card && header) return;
            }
        }
        string Fit(string text, Font font, float available, bool ellipsis)
        {
            if (available <= 0) return "";
            if (OverlayTextRenderer.Measure(measure, text, font).Width <= available) return text;
            var reserve = ellipsis ? OverlayTextRenderer.Measure(measure, "…", font).Width : 0;
            var low = 0; var high = text.Length;
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (OverlayTextRenderer.Measure(measure, text[..mid], font).Width <= available - reserve) low = mid; else high = mid - 1;
            }
            if (low > 0 && char.IsHighSurrogate(text[low - 1])) low--;
            return low > 0 ? text[..low] + (ellipsis ? "…" : "") : "";
        }
        void Ellipsis(Font font)
        {
            stop = true;
            var size = OverlayTextRenderer.Measure(measure, "…", font);
            var bounds = new RectangleF(Math.Max(startX, Math.Min(x, right - size.Width)), Math.Max(pad, Math.Min(y, maxHeight - pad - font.GetHeight(measure))), size.Width + 2, font.GetHeight(measure));
            pieces.Add(new Piece { Text = "…", Font = font, Bounds = bounds, Color = OverlayTextRenderer.Color(card ? style.TextColor : region.Color) });
            furthest = Math.Max(furthest, bounds.Right);
        }
    }

    public static Bitmap RenderInteractions(IReadOnlyList<LiveEvent> values, OverlayRegionSettings region, DashboardSettings style, BiliImageCache cache, int width, int maxHeight, long totalCount = 0)
    {
        totalCount = Math.Max(totalCount, values.Count);
        if (values.Count == 1 && totalCount == 1) return Render(values[0], region, style, cache, width, maxHeight, true);
        var pad = 10;
        using var titleFont = OverlayTextRenderer.CreateFont(region.FontFamily, 12, true);
        var rowHeight = Math.Max(36, region.FontSize + 16);
        var available = Math.Max(0, maxHeight - 34 - pad);
        var shown = Math.Min(values.Count, available / rowHeight);
        if (shown < totalCount) shown = Math.Max(0, Math.Min(shown, (available - 20) / rowHeight));
        var height = Math.Min(maxHeight, 34 + shown * rowHeight + (shown < totalCount ? 22 : 0) + pad);
        var bitmap = new Bitmap(width, Math.Max(36, height), PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var accent = OverlayTextRenderer.Color(region.Color);
        DrawBackground(graphics, bitmap.Size, region, accent);
        OverlayTextRenderer.Draw(graphics, "近期互动", titleFont, new(pad, 10), accent, false);
        var y = 32;
        for (var i = 0; i < shown; i++)
        {
            using var row = Render(values[i], region with { Color = style.TextColor, Outline = false }, style, cache, width - pad * 2, rowHeight, false);
            graphics.DrawImageUnscaled(row, pad, y); y += rowHeight;
        }
        if (shown < totalCount) OverlayTextRenderer.Draw(graphics, $"另有 {totalCount - shown} 条互动 · 概览可查看记录", titleFont, new(pad, y), OverlayTextRenderer.Color(style.TextColor), false);
        return bitmap;
    }

    internal static void DrawBackground(Graphics graphics, Size size, OverlayRegionSettings region, Color accent)
    {
        if (region.BackgroundOpacity <= 0) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = OverlayTextRenderer.RoundedRectangle(new(0.5f, 0.5f, size.Width - 1, size.Height - 1), 8);
        using var fill = new SolidBrush(OverlayTextRenderer.Color(region.BackgroundColor, region.BackgroundOpacity));
        graphics.FillPath(fill, shape);
        using var border = new Pen(Color.FromArgb((int)(160 * region.BackgroundOpacity), accent), 1); graphics.DrawPath(border, shape);
    }
    private static string Label(LiveEvent value) => value.Type switch
    {
        LiveEventType.Gift => "礼物", LiveEventType.Guard => "上舰", LiveEventType.SuperChat => "SC" + Price(value)
            + (value.Extra.TryGetValue("scroll_part", out var part) && part is string { Length: > 0 } ? $" · {part}" : ""),
        LiveEventType.Enter => "进房 / 互动", LiveEventType.Online => "房间人气", LiveEventType.Rank => "高能榜", _ => "弹幕",
    };
    private static string Price(LiveEvent value)
    {
        if (value.Extra.TryGetValue("scroll_price", out var label) && label is string saved) return saved;
        if (value.Extra.TryGetValue("price", out var price) && price is not null) return $" · ¥{price}";
        var space = value.Content.IndexOf(' ');
        return value.Content.StartsWith('¥') && space is > 1 and < 16 ? " · " + value.Content[..space] : "";
    }
    private static string Content(LiveEvent value)
    {
        if (value.Type == LiveEventType.Rank && value.Content.StartsWith("高能榜:", StringComparison.Ordinal)) return value.Content[4..].TrimStart();
        if (value.Type == LiveEventType.SuperChat && !value.Extra.ContainsKey("scroll_part") && Price(value).Length > 0 && value.Content.StartsWith('¥') && value.Content.IndexOf(' ') is var space && space > 0) return value.Content[(space + 1)..];
        return value.Content;
    }
    private sealed class Piece
    {
        public string Text = "";
        public Font? Font;
        public Bitmap? Image;
        public RectangleF Bounds;
        public Color Color;
        public bool Avatar, Badge, Clip;
    }
}
