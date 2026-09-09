using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AbletonManager
{
    /// <summary>Один сторонний плагин сета как цель для отключения — вместе со всеми своими копиями.</summary>
    public sealed class AlsPluginSlot
    {
        public string Uid = "";
        public string Name = "";
        public PluginKind Kind;
        public int Count = 1;          // сколько раз стоит в сете

        /// <summary>
        /// Разработчик — только когда он и правда разработчик. У VST2 на этом месте
        /// оказывается папка, в которой лежит .dll («Eff», «Gen»), и показывать её как
        /// вендора значит врать (см. AlsFile.ParseBrowserPath).
        /// </summary>
        public string Vendor = "";

        public string Format { get { return Kind == PluginKind.Vst3 ? "VST3" : "VST2"; } }

        public string Label
        {
            get { return Count > 1 ? Name + " ×" + Count.ToString(CultureInfo.InvariantCulture) : Name; }
        }
    }

    /// <summary>
    /// Копия сета, в которой Live не узнаёт выбранные плагины.
    ///
    /// Плагин в .als опознаётся идентификатором, а не именем и не файлом:
    ///
    ///     &lt;Vst3PluginInfo&gt;…&lt;Uid&gt;&lt;Fields.0 Value="-1412567295" /&gt;…&lt;/Uid&gt;
    ///     &lt;VstPluginInfo&gt;&lt;Path Value="…\Serum_x64.dll" /&gt;&lt;UniqueId Value="1483109208" /&gt;
    ///
    /// Подменяем ровно эти значения — и Live честно скажет «плагин не найден», покажет
    /// на его месте заглушку с прежним именем и загрузит сет дальше. Ничего не удаляем:
    /// узел устройства, его место в цепочке, автоматизация, идентификаторы и даже блоб
    /// сохранённого пресета остаются на месте байт в байт. Это принципиально — вырезать
    /// плагин из .als значит трогать DeviceChain, automation и ID разом, и такая правка
    /// ломает сет надёжнее, чем сам сломанный плагин.
    ///
    /// Правка точечная: меняются только эти Value, всё остальное копируется байт в байт.
    /// Оригинал не трогается никогда — пишем в отдельный файл, который зовущая сторона
    /// потом удалит.
    ///
    /// Идём потоком, строка за строкой, и держим в памяти только текущий узел устройства.
    /// Поднимать распакованный XML целиком нельзя: у сета на этой машине он разворачивается
    /// в 177 МБ, то есть 350 МБ строкой .NET, и со сборкой результата это под гигабайт на
    /// одну правку. Узел же — 0.2 МБ в худшем случае, а строки в .als короче 230 байт.
    /// </summary>
    public static class AlsPatch
    {
        /// <summary>
        /// Метка вместо Fields.0 у VST3: «Aliv» в ASCII. Совпасть с настоящим плагином
        /// не может — она заменяет только старшее слово, а остальные три поля остаются
        /// прежними, так что два отключённых плагина не схлопываются в один и тот же
        /// несуществующий идентификатор.
        /// </summary>
        const int Vst3Marker = 0x416C6976;

        /// <summary>То же для VST2: идентификатор там одно число, поэтому портим его xor-ом.</summary>
        const int Vst2Marker = 0x416C6976;

        /// <summary>
        /// Чем дописывается путь к .dll у VST2. Одного сломанного UniqueId мало: Live
        /// умеет поднять VST2 и по файлу, и тогда плагин загрузился бы как ни в чём не
        /// бывало — то есть проба не проверила бы ничего.
        /// </summary>
        const string DisabledSuffix = ".alive-disabled";

        // ------------------------------------------------------------------ цели

        /// <summary>
        /// Сторонние плагины сета, которые можно отключить, — по одному на идентификатор.
        /// Без идентификатора плагин не адресуется (так бывает у Audio Unit: на Windows
        /// их всё равно не поднять), такие сюда не попадают.
        /// </summary>
        public static List<AlsPluginSlot> Targets(AlsInfo info)
        {
            List<AlsPluginSlot> list = new List<AlsPluginSlot>();
            if (info == null) return list;

            Dictionary<string, AlsPluginSlot> byUid =
                new Dictionary<string, AlsPluginSlot>(StringComparer.OrdinalIgnoreCase);

            foreach (PluginRef p in info.Plugins)
            {
                if (p.Uid.Length == 0) continue;
                if (p.Kind != PluginKind.Vst2 && p.Kind != PluginKind.Vst3) continue;

                AlsPluginSlot slot;
                if (byUid.TryGetValue(p.Uid, out slot)) { slot.Count++; continue; }

                slot = new AlsPluginSlot();
                slot.Uid = p.Uid;
                slot.Name = p.Name;
                slot.Vendor = p.VendorConfident ? p.Manufacturer : "";
                slot.Kind = p.Kind;
                byUid[p.Uid] = slot;
                list.Add(slot);
            }

            list.Sort(delegate (AlsPluginSlot a, AlsPluginSlot b)
            { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            return list;
        }

        /// <summary>Сколько плагинов сета отключить нельзя — не по чему опознать.</summary>
        public static int Unaddressable(AlsInfo info)
        {
            if (info == null) return 0;
            int n = 0;
            foreach (PluginRef p in info.Plugins)
                if (p.Uid.Length == 0) n++;
            return n;
        }

        // ------------------------------------------------------------------ правка

        /// <summary>
        /// Пишет в dst копию src, где перечисленные плагины обезличены. Возвращает,
        /// сколько узлов устройств тронуто — ноль означает, что ни один из заказанных
        /// плагинов в файле не нашёлся, и запускать такую пробу бессмысленно.
        ///
        /// inv нужен только чтобы подменённый идентификатор случайно не совпал с другим
        /// установленным плагином: Live тогда молча подставила бы чужое устройство.
        /// </summary>
        public static int Neutralize(string src, string dst, ICollection<string> uids, PluginInventory inv)
        {
            if (uids == null || uids.Count == 0)
                throw new ArgumentException("nothing to neutralize", "uids");

            HashSet<string> wanted = new HashSet<string>(uids, StringComparer.OrdinalIgnoreCase);
            int patched = 0;

            using (FileStream fin = new FileStream(src, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite, 64 * 1024))
            using (GZipStream gin = new GZipStream(fin, CompressionMode.Decompress))
            // detectEncodingFromByteOrderMarks: false — иначе BOM был бы съеден на чтении
            // и не записан обратно, а копия должна отличаться от оригинала ровно теми
            // значениями, которые мы меняем, и ничем больше.
            using (StreamReader rin = new StreamReader(gin, new UTF8Encoding(false), false, 64 * 1024))

            using (FileStream fout = new FileStream(dst, FileMode.Create, FileAccess.Write,
                                                    FileShare.None, 64 * 1024))
            using (GZipStream gout = new GZipStream(fout, CompressionMode.Compress))
            using (StreamWriter wout = new StreamWriter(gout, new UTF8Encoding(false), 64 * 1024))
            {
                LineReader lines = new LineReader(rin);
                StringBuilder node = null;
                string closeTag = null;
                PluginKind kind = PluginKind.Vst3;

                string line;
                while ((line = lines.Next()) != null)
                {
                    if (node == null)
                    {
                        int which = Opens(line);
                        if (which < 0) { wout.Write(line); continue; }

                        node = new StringBuilder(line);
                        closeTag = CloseTags[which];
                        kind = which == 0 ? PluginKind.Vst3 : PluginKind.Vst2;
                    }
                    else node.Append(line);

                    // Закрывающий тег ищем в текущей строке, а не во всём накопленном:
                    // иначе на каждую строку узла пересобиралась бы вся его строка целиком.
                    // Работает и когда узел уместился в одну строку, и когда он растянут
                    // на тысячи, — а разорванным между строками тег не бывает.
                    if (line.IndexOf(closeTag, StringComparison.Ordinal) < 0) continue;

                    string text = node.ToString();
                    node = null;

                    string uid = UidOf(text, kind);
                    if (uid.Length > 0 && wanted.Contains(uid))
                    {
                        wout.Write(Rewrite(text, kind, uid, inv));
                        patched++;
                    }
                    else wout.Write(text);
                }

                // Файл оборвался посреди узла — пишем как есть, чтобы не потерять хвост.
                if (node != null) wout.Write(node.ToString());
            }

            Diag.Line("rescue: patched " + patched + " device(s) in " + Path.GetFileName(dst));
            return patched;
        }

        // -------------------------------------------------------- поиск узлов устройств

        static readonly string[] OpenTags = { "<Vst3PluginInfo", "<VstPluginInfo" };
        static readonly string[] CloseTags = { "</Vst3PluginInfo>", "</VstPluginInfo>" };

        /// <summary>
        /// Открывает ли строка узел описания плагина: 0 — VST3, 1 — VST2, иначе -1.
        ///
        /// Простым поиском подстроки, а не разбором XML: файл машинный, эти узлы не
        /// вкладываются друг в друга, зато пересборка документа через XmlWriter
        /// переписала бы и те байты, которых правка не касается, — а вся затея в том,
        /// чтобы менять только идентификаторы. Внутрь блобов ProcessorState и Buffer
        /// поиск не попадёт: там только шестнадцатеричные цифры, скобок в них нет.
        /// </summary>
        static int Opens(string line)
        {
            // Порядок важен: «&lt;Vst3PluginInfo» тоже содержит «PluginInfo», но не
            // «&lt;VstPluginInfo» — а вот проверять VST2 первым было бы всё равно неверно
            // на строке, где стоят оба (в разметке Live такого нет, но цена нулевая).
            for (int i = 0; i < OpenTags.Length; i++)
                if (line.IndexOf(OpenTags[i], StringComparison.Ordinal) >= 0) return i;
            return -1;
        }

        /// <summary>
        /// Строка вместе с её концом строки. StreamReader.ReadLine концы съедает, и
        /// пришлось бы угадывать, что там было: в .als это «\r\n», в других файлах Live
        /// (журнал) — одиночный «\n», а копия обязана совпадать с оригиналом байт в байт
        /// везде, кроме подменённых значений.
        /// </summary>
        /// <summary>Внутренний, а не приватный: тем же чтением пользуется AlsSamplePatch.</summary>
        internal sealed class LineReader
        {
            readonly TextReader _r;
            readonly char[] _buf = new char[64 * 1024];
            int _len, _pos;

            public LineReader(TextReader r) { _r = r; }

            public string Next()
            {
                StringBuilder carry = null;
                while (true)
                {
                    if (_pos >= _len)
                    {
                        _len = _r.Read(_buf, 0, _buf.Length);
                        _pos = 0;
                        if (_len <= 0)
                            return carry != null && carry.Length > 0 ? carry.ToString() : null;
                    }

                    int nl = Array.IndexOf(_buf, '\n', _pos, _len - _pos);
                    if (nl >= 0)
                    {
                        string s = new string(_buf, _pos, nl - _pos + 1);
                        _pos = nl + 1;
                        if (carry == null) return s;
                        carry.Append(s);
                        return carry.ToString();
                    }

                    if (carry == null) carry = new StringBuilder();
                    carry.Append(_buf, _pos, _len - _pos);
                    _pos = _len;
                }
            }
        }

        // ------------------------------------------------------------ опознание плагина

        /// <summary>
        /// Идентификатор из узла — в том же виде, что и у AlsFile и у базы самой Live.
        /// Считает его тот же PluginRef.FinishUid, чтобы формула жила в одном месте:
        /// разъехавшись, эти два разбора отключали бы не тот плагин, который показали.
        /// </summary>
        static string UidOf(string node, PluginKind kind)
        {
            if (kind == PluginKind.Vst2)
            {
                long id;
                if (!FirstLong(node, "<UniqueId Value=\"", out id)) return "";
                PluginRef r = new PluginRef();
                r.Kind = PluginKind.Vst2;
                r.Vst2UniqueId = id;
                r.FinishUid();
                return r.Uid;
            }

            // У VST3 блоков <Uid> в узле два — свой у пресета и свой у устройства, и
            // значения в них одинаковые. Берём последний: это тот, что лежит прямо в
            // Vst3PluginInfo, ровно как его читает AlsFile.
            int last = node.LastIndexOf("<Uid>", StringComparison.Ordinal);
            if (last < 0) return "";
            int close = node.IndexOf("</Uid>", last, StringComparison.Ordinal);
            if (close < 0) return "";

            PluginRef v3 = new PluginRef();
            v3.Kind = PluginKind.Vst3;
            string block = node.Substring(last, close - last);
            for (int i = 0; i < 4; i++)
            {
                long f;
                if (!FirstLong(block, "<Fields." + i + " Value=\"", out f)) return "";
                v3.Vst3Fields[i] = unchecked((int)f);
                v3.Vst3FieldCount++;
            }
            v3.FinishUid();
            return v3.Uid;
        }

        // ------------------------------------------------------------------ подмена

        static string Rewrite(string node, PluginKind kind, string uid, PluginInventory inv)
        {
            if (kind == PluginKind.Vst2)
            {
                long id;
                if (!FirstLong(node, "<UniqueId Value=\"", out id)) return node;

                int fake = FreeVst2Id(unchecked((int)id), inv);
                string s = ReplaceAll(node, "<UniqueId Value=\"",
                                      fake.ToString(CultureInfo.InvariantCulture));
                return SuffixPaths(s);
            }

            int marker = FreeVst3Marker(node, inv);
            return ReplaceAll(node, "<Fields.0 Value=\"",
                              marker.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Метка, которая ни на что установленное не похожа. Проверка не паранойя:
        /// совпади подменённый идентификатор с другим плагином — Live не сказала бы
        /// «не найден», а подставила бы чужое устройство, и проба показала бы неправду.
        /// </summary>
        static int FreeVst3Marker(string node, PluginInventory inv)
        {
            int last = node.LastIndexOf("<Uid>", StringComparison.Ordinal);
            if (last < 0) return Vst3Marker;

            PluginRef probe = new PluginRef();
            probe.Kind = PluginKind.Vst3;
            for (int i = 1; i < 4; i++)
            {
                long f;
                if (!FirstLong(node.Substring(last), "<Fields." + i + " Value=\"", out f)) return Vst3Marker;
                probe.Vst3Fields[i] = unchecked((int)f);
            }
            probe.Vst3FieldCount = 4;

            for (int bump = 0; bump < 64; bump++)
            {
                int candidate = unchecked(Vst3Marker + bump);
                probe.Vst3Fields[0] = candidate;
                probe.FinishUid();
                if (inv == null || inv.ByUid(probe.Uid) == null) return candidate;
            }
            return Vst3Marker;
        }

        static int FreeVst2Id(int original, PluginInventory inv)
        {
            for (int bump = 0; bump < 64; bump++)
            {
                int candidate = unchecked(original ^ (Vst2Marker + bump));
                if (candidate == original) continue;
                string uid = "vst2:" + candidate.ToString(CultureInfo.InvariantCulture);
                if (inv == null || inv.ByUid(uid) == null) return candidate;
            }
            return unchecked(original ^ Vst2Marker);
        }

        /// <summary>Дописать «.alive-disabled» ко всем путям узла — файла с таким именем нет.</summary>
        static string SuffixPaths(string node)
        {
            const string tag = "<Path Value=\"";
            StringBuilder sb = new StringBuilder(node.Length + 32);
            int pos = 0;
            while (true)
            {
                int at = node.IndexOf(tag, pos, StringComparison.Ordinal);
                if (at < 0) break;
                int from = at + tag.Length;
                int close = node.IndexOf('"', from);
                if (close < 0) break;

                sb.Append(node, pos, close - pos).Append(DisabledSuffix);
                pos = close;
            }
            sb.Append(node, pos, node.Length - pos);
            return sb.ToString();
        }

        /// <summary>Заменить значение атрибута во всех вхождениях тега внутри узла.</summary>
        static string ReplaceAll(string node, string tag, string value)
        {
            StringBuilder sb = new StringBuilder(node.Length + 32);
            int pos = 0;
            while (true)
            {
                int at = node.IndexOf(tag, pos, StringComparison.Ordinal);
                if (at < 0) break;
                int from = at + tag.Length;
                int close = node.IndexOf('"', from);
                if (close < 0) break;

                sb.Append(node, pos, from - pos).Append(value);
                pos = close;
            }
            sb.Append(node, pos, node.Length - pos);
            return sb.ToString();
        }

        static bool FirstLong(string s, string tag, out long value)
        {
            value = 0;
            int at = s.IndexOf(tag, StringComparison.Ordinal);
            if (at < 0) return false;
            int from = at + tag.Length;
            int close = s.IndexOf('"', from);
            if (close < 0) return false;
            return long.TryParse(s.Substring(from, close - from), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out value);
        }

    }
}
