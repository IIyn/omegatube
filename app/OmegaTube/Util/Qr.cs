using System;
using System.Collections.Generic;
using System.Text;

namespace OmegaTube.Util
{
    // Managed QR code encoder (byte mode, ECC low or medium), a port of the encoding steps of Nayuki's
    // qrcodegen (MIT). The bug report screen needs it when libomegacore.so itself fails to load, so it
    // cannot go through NativeCore.QrEncode.
    public static class Qr
    {
        // [low, medium][version]
        private static readonly int[][] EccPerBlock =
        {
            new[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        };
        private static readonly int[][] Blocks =
        {
            new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
            new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        };
        private static readonly int[] FormatEcc = { 1, 0 };  // format bits of low, medium

        // Rows of '1' (dark) / '0', like YouTube.Qr. Empty when the text does not fit in version 40.
        public static List<string> Encode(string text, bool medium = false)
        {
            var rows = new List<string>();
            bool[,] m = Build(Encoding.UTF8.GetBytes(text ?? ""), medium ? 1 : 0);
            if (m == null)
            {
                return rows;
            }
            int size = m.GetLength(0);
            for (int y = 0; y < size; y++)
            {
                var row = new StringBuilder(size);
                for (int x = 0; x < size; x++)
                {
                    row.Append(m[y, x] ? '1' : '0');
                }
                rows.Add(row.ToString());
            }
            return rows;
        }

        // Highest byte count that still fits in `version` (to size a payload).
        public static int Capacity(int version, bool medium = false)
        {
            return DataCodewords(version, medium ? 1 : 0) - (version < 10 ? 2 : 3);
        }

        private static bool[,] Build(byte[] data, int ecl)
        {
            int version = 1;
            for (; version <= 40; version++)
            {
                if (4 + (version < 10 ? 8 : 16) + data.Length * 8 <= DataCodewords(version, ecl) * 8)
                {
                    break;
                }
            }
            if (version > 40)
            {
                return null;
            }

            var bits = new List<bool>();
            AppendBits(bits, 4, 4);  // byte mode
            AppendBits(bits, data.Length, version < 10 ? 8 : 16);
            foreach (byte b in data)
            {
                AppendBits(bits, b, 8);
            }
            int capacity = DataCodewords(version, ecl) * 8;
            AppendBits(bits, 0, Math.Min(4, capacity - bits.Count));
            AppendBits(bits, 0, (8 - bits.Count % 8) % 8);
            for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
            {
                AppendBits(bits, pad, 8);
            }
            var codewords = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++)
            {
                if (bits[i])
                {
                    codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
                }
            }

            var q = new Symbol(version);
            q.DrawFunctionPatterns(ecl);
            q.DrawCodewords(AddEcc(codewords, version, ecl));
            int best = 0;
            long minPenalty = long.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                q.ApplyMask(mask);
                q.DrawFormatBits(ecl, mask);
                long penalty = q.Penalty();
                if (penalty < minPenalty)
                {
                    best = mask;
                    minPenalty = penalty;
                }
                q.ApplyMask(mask);  // XOR: undoes it
            }
            q.ApplyMask(best);
            q.DrawFormatBits(ecl, best);
            return q.Modules;
        }

        private static void AppendBits(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        private static int RawDataModules(int ver)
        {
            int result = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int numAlign = ver / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (ver >= 7)
                {
                    result -= 36;
                }
            }
            return result;
        }

        private static int DataCodewords(int ver, int ecl) => RawDataModules(ver) / 8 - EccPerBlock[ecl][ver] * Blocks[ecl][ver];

        // Splits the data into blocks, appends each block's Reed-Solomon ECC, interleaves.
        private static byte[] AddEcc(byte[] data, int ver, int ecl)
        {
            int numBlocks = Blocks[ecl][ver];
            int eccLen = EccPerBlock[ecl][ver];
            int raw = RawDataModules(ver) / 8;
            int numShort = numBlocks - raw % numBlocks;
            int shortLen = raw / numBlocks;
            byte[] divisor = RsDivisor(eccLen);
            var blocks = new List<byte[]>();
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int datLen = shortLen - eccLen + (i < numShort ? 0 : 1);
                var block = new byte[shortLen + 1];
                Array.Copy(data, k, block, 0, datLen);
                byte[] ecc = RsRemainder(data, k, datLen, divisor);
                k += datLen;
                // Short blocks keep a dummy byte at datLen so every block has the same layout.
                Array.Copy(ecc, 0, block, shortLen + 1 - eccLen, eccLen);
                blocks.Add(block);
            }
            var result = new List<byte>(raw);
            for (int i = 0; i < shortLen + 1; i++)
            {
                for (int j = 0; j < numBlocks; j++)
                {
                    if (i != shortLen - eccLen || j >= numShort)
                    {
                        result.Add(blocks[j][i]);
                    }
                }
            }
            return result.ToArray();
        }

        private static byte[] RsDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = RsMultiply(result[j], root);
                    if (j + 1 < degree)
                    {
                        result[j] ^= result[j + 1];
                    }
                }
                root = RsMultiply(root, 0x02);
            }
            return result;
        }

        private static byte[] RsRemainder(byte[] data, int offset, int length, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            for (int n = 0; n < length; n++)
            {
                int factor = data[offset + n] ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] ^= RsMultiply(divisor[i], factor);
                }
            }
            return result;
        }

        private static byte RsMultiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        private class Symbol
        {
            public readonly bool[,] Modules;
            private readonly bool[,] isFunction;
            private readonly int size, version;

            public Symbol(int version)
            {
                this.version = version;
                size = version * 4 + 17;
                Modules = new bool[size, size];
                isFunction = new bool[size, size];
            }

            private void Set(int x, int y, bool dark)
            {
                Modules[y, x] = dark;
                isFunction[y, x] = true;
            }

            public void DrawFunctionPatterns(int ecl)
            {
                for (int i = 0; i < size; i++)
                {
                    Set(6, i, i % 2 == 0);
                    Set(i, 6, i % 2 == 0);
                }
                DrawFinder(3, 3);
                DrawFinder(size - 4, 3);
                DrawFinder(3, size - 4);
                int[] align = AlignmentPositions();
                int n = align.Length;
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0))
                        {
                            DrawAlignment(align[i], align[j]);
                        }
                    }
                }
                DrawFormatBits(ecl, 0);
                DrawVersion();
            }

            private void DrawFinder(int x, int y)
            {
                for (int dy = -4; dy <= 4; dy++)
                {
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && xx < size && yy >= 0 && yy < size)
                        {
                            Set(xx, yy, dist != 2 && dist != 4);
                        }
                    }
                }
            }

            private void DrawAlignment(int x, int y)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        Set(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }

            private int[] AlignmentPositions()
            {
                if (version == 1)
                {
                    return new int[0];
                }
                int n = version / 7 + 2;
                int step = (version * 8 + n * 3 + 5) / (n * 4 - 4) * 2;
                var result = new int[n];
                result[0] = 6;
                for (int i = n - 1, pos = size - 7; i >= 1; i--, pos -= step)
                {
                    result[i] = pos;
                }
                return result;
            }

            public void DrawFormatBits(int ecl, int mask)
            {
                int data = FormatEcc[ecl] << 3 | mask;
                int rem = data;
                for (int i = 0; i < 10; i++)
                {
                    rem = (rem << 1) ^ ((rem >> 9) * 0x537);
                }
                int bits = (data << 10 | rem) ^ 0x5412;
                for (int i = 0; i <= 5; i++)
                {
                    Set(8, i, Bit(bits, i));
                }
                Set(8, 7, Bit(bits, 6));
                Set(8, 8, Bit(bits, 7));
                Set(7, 8, Bit(bits, 8));
                for (int i = 9; i < 15; i++)
                {
                    Set(14 - i, 8, Bit(bits, i));
                }
                for (int i = 0; i < 8; i++)
                {
                    Set(size - 1 - i, 8, Bit(bits, i));
                }
                for (int i = 8; i < 15; i++)
                {
                    Set(8, size - 15 + i, Bit(bits, i));
                }
                Set(8, size - 8, true);
            }

            private void DrawVersion()
            {
                if (version < 7)
                {
                    return;
                }
                int rem = version;
                for (int i = 0; i < 12; i++)
                {
                    rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                }
                int bits = version << 12 | rem;
                for (int i = 0; i < 18; i++)
                {
                    int a = size - 11 + i % 3, b = i / 3;
                    Set(a, b, Bit(bits, i));
                    Set(b, a, Bit(bits, i));
                }
            }

            public void DrawCodewords(byte[] data)
            {
                int i = 0;
                for (int right = size - 1; right >= 1; right -= 2)
                {
                    if (right == 6)
                    {
                        right = 5;
                    }
                    for (int vert = 0; vert < size; vert++)
                    {
                        for (int j = 0; j < 2; j++)
                        {
                            int x = right - j;
                            bool upward = ((right + 1) & 2) == 0;
                            int y = upward ? size - 1 - vert : vert;
                            if (!isFunction[y, x] && i < data.Length * 8)
                            {
                                Modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                                i++;
                            }
                        }
                    }
                }
            }

            public void ApplyMask(int mask)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        bool invert;
                        switch (mask)
                        {
                            case 0: invert = (x + y) % 2 == 0; break;
                            case 1: invert = y % 2 == 0; break;
                            case 2: invert = x % 3 == 0; break;
                            case 3: invert = (x + y) % 3 == 0; break;
                            case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                            case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                            case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                            default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                        }
                        if (invert && !isFunction[y, x])
                        {
                            Modules[y, x] = !Modules[y, x];
                        }
                    }
                }
            }

            // The spec's penalty rules (long runs, 2x2 blocks, finder-like patterns, dark/light balance).
            public long Penalty()
            {
                long result = 0;
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int a = 0; a < size; a++)
                    {
                        bool runColor = false;
                        int run = 0;
                        var history = new int[7];
                        for (int b = 0; b < size; b++)
                        {
                            bool c = pass == 0 ? Modules[a, b] : Modules[b, a];
                            if (c == runColor)
                            {
                                run++;
                                if (run == 5)
                                {
                                    result += 3;
                                }
                                else if (run > 5)
                                {
                                    result++;
                                }
                            }
                            else
                            {
                                AddHistory(run, history);
                                if (!runColor)
                                {
                                    result += CountFinderLike(history) * 40;
                                }
                                runColor = c;
                                run = 1;
                            }
                        }
                        if (runColor)
                        {
                            AddHistory(run, history);
                            run = 0;
                        }
                        AddHistory(run + size, history);
                        result += CountFinderLike(history) * 40;
                    }
                }
                int dark = 0;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        bool c = Modules[y, x];
                        dark += c ? 1 : 0;
                        if (x < size - 1 && y < size - 1 && c == Modules[y, x + 1] && c == Modules[y + 1, x] && c == Modules[y + 1, x + 1])
                        {
                            result += 3;
                        }
                    }
                }
                int total = size * size;
                int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
                return result + k * 10;
            }

            private void AddHistory(int run, int[] history)
            {
                if (history[0] == 0)
                {
                    run += size;  // light border before the first run
                }
                Array.Copy(history, 0, history, 1, history.Length - 1);
                history[0] = run;
            }

            private static int CountFinderLike(int[] h)
            {
                int n = h[1];
                bool core = n > 0 && h[2] == n && h[3] == n * 3 && h[4] == n && h[5] == n;
                return (core && h[0] >= n * 4 && h[6] >= n ? 1 : 0) + (core && h[6] >= n * 4 && h[0] >= n ? 1 : 0);
            }

            private static bool Bit(int x, int i) => ((x >> i) & 1) != 0;
        }
    }
}
