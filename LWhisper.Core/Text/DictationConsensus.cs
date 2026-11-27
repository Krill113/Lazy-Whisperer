using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LWhisper.Core.Text
{

/// <summary>Точечные поправки к исходной диктовке по двум чтениям того же звука.</summary>
public static class DictationConsensus
{
    private static readonly Regex Word = new Regex(@"[\p{L}\p{Nd}_]+", RegexOptions.Compiled);
    private static readonly Regex Literal = new Regex("\"[^\"\r\n]*\"|«[^»\r\n]*»|`[^`\r\n]*`|\\S*(?:[/\\\\@_]|\\.[a-zA-Z0-9])\\S*", RegexOptions.Compiled);
    private static readonly Regex Sensitive = new Regex(@"^(?:не|ни|нет|без|нельзя|плюс|минус|ноль|нуль|один|одна|два|две|три|четыре|пять|шесть|семь|восемь|девять|десять|сто)$",RegexOptions.IgnoreCase);
    private sealed class Tokens
    {
        internal readonly Match[] M;
        internal readonly string[] W;
        internal Tokens(string text) { M = Word.Matches(text).Cast<Match>().ToArray(); W = M.Select(m => m.Value.ToLowerInvariant().Replace('ё','е')).ToArray(); }
    }

    /// <summary>Возвращает исходный текст при отсутствии согласованного безопасного изменения.</summary>
    public static string Apply(string baseline, string fullTurbo, string parakeet)
    {
        if (new[]{baseline,fullTurbo,parakeet}.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 10000)) return baseline;
        var a = new Tokens(baseline); var b = new Tokens(fullTurbo); var c = new Tokens(parakeet);
        if (new[]{a,b,c}.Any(t => t.W.Length == 0 || t.W.Length > 700)) return baseline;
        var turbo = Diff(a.W,b.W); var other = Diff(a.W,c.W);
        // При несовпадающем чтении числа кратчайшее выравнивание может разрезать копию.
        // Для длинного удаления требуем тот же цельный разрыв между точными якорями в обоих чтениях.
        var left = AnchorGaps(a.W,b.W); var right = AnchorGaps(a.W,c.W);
        foreach (var gap in left.Where(g => g.k == g.l && g.j-g.i >= 4 && Duplicated(a.W,g.i,g.j)))
        {
            int index = right.FindIndex(v => v.i==gap.i && v.j==gap.j && v.k==v.l);
            if (index < 0 || !Anchored(a.W,b.W,gap.i,gap.j,gap.k) || !Anchored(a.W,c.W,gap.i,gap.j,right[index].k)) continue;
            turbo.RemoveAll(v => v.i < gap.j && v.j > gap.i);
            other.RemoveAll(v => v.i < gap.j && v.j > gap.i);
            turbo.Add(gap); other.Add(right[index]);
        }
        turbo.Sort((x,y) => x.i.CompareTo(y.i));
        var literals = Literal.Matches(baseline).Cast<Match>().ToArray();
        string result = baseline;
        foreach (var e in turbo.AsEnumerable().Reverse())
        {
            var (i,j,k,l) = e;
            var matches = other.Where(v => v.i == i && v.j == j && v.l-v.k == l-k &&
                b.W.Skip(k).Take(l-k).SequenceEqual(c.W.Skip(v.k).Take(v.l-v.k))).ToArray();
            if (matches.Length != 1) continue;
            if (i == j)
            {
                // Восстанавливаем единственный произнесённый повтор, если оба чтения сохранили его.
                if (l-k!=1 || i==0 || i>=a.W.Length || k==0 || l>=b.W.Length || matches[0].k==0 || matches[0].l>=c.W.Length || a.W[i-1]!=b.W[k] || a.W[i]==b.W[k] || (i>=2 && a.W[i-2]==b.W[k])) continue;
                if (a.W[i]!=b.W[l] || a.W[i]!=c.W[matches[0].l] || a.W[i-1]!=b.W[k-1] || a.W[i-1]!=c.W[matches[0].k-1] || !Plain(a.M[i-1],baseline)) continue;
                var pos=a.M[i-1].Index+a.M[i-1].Length;
                if (literals.Any(m=>m.Index<pos && pos<=m.Index+m.Length)) continue;
                result=result.Insert(pos, ", "+a.M[i-1].Value.ToLowerInvariant());
                continue;
            }
            int start = a.M[i].Index, end = a.M[j-1].Index + a.M[j-1].Length;
            if (literals.Any(m => m.Index < end && start < m.Index+m.Length)) continue;
            string replacement;
            if (k == l)
            {
                if (!Duplicated(a.W,i,j) || !Anchored(a.W,b.W,i,j,k) || !Anchored(a.W,c.W,i,j,matches[0].k)) continue;
                // Удаляем также разделитель между копиями; оставляем исходную пунктуацию после последней.
                if (i == 0 || j == a.W.Length) continue;
                start = a.M[i-1].Index + a.M[i-1].Length;
                if (baseline.Substring(start,end-start).Any(ch => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch) && ".,!?:;".IndexOf(ch)<0)) continue;
                replacement = "";
            }
            else
            {
                if (j-i != 1 || l-k != 1 || !Plain(a.M[i],baseline) || !Plain(b.M[k],fullTurbo, char.IsUpper(a.M[i].Value[0])) || !Plain(c.M[matches[0].k],parakeet, char.IsUpper(a.M[i].Value[0]))) continue;
                // Не превращать два соседних понятия в повтор одного слова.
                if (a.W.Skip(Math.Max(0,i-4)).Take(Math.Min(i,4)).Contains(b.W[k]) || a.W.Skip(j).Take(4).Contains(b.W[k])) continue;
                replacement = b.M[k].Value;
            }
            result = result.Remove(start,end-start).Insert(start,replacement);
        }
        return result;
    }

    private static bool Plain(Match m,string text,bool sentenceCase = false) => m.Value.All(ch => (ch >= 'а' && ch <= 'я') || (ch >= 'А' && ch <= 'Я') || ch == 'ё' || ch == 'Ё') &&
        !Sensitive.IsMatch(m.Value) && (!char.IsUpper(m.Value[0]) || (m.Value.Skip(1).All(char.IsLower) &&
        (sentenceCase || m.Index == 0 || Regex.IsMatch(text.Substring(0,m.Index),@"[.!?]\s*$")))) &&
        (m.Index==0 || text[m.Index-1]!='-') && (m.Index+m.Length==text.Length || text[m.Index+m.Length]!='-');

    private static bool Anchored(string[] a,string[] witness,int i,int j,int k)
    {
        int left=Math.Min(2,i), right=Math.Min(2,a.Length-j);
        return left>0 && right>0 && k>=left && witness.Length-k>=right &&
            a.Skip(i-left).Take(left).SequenceEqual(witness.Skip(k-left).Take(left)) && a.Skip(j).Take(right).SequenceEqual(witness.Skip(k).Take(right));
    }

    private static bool Duplicated(string[] a,int i,int j)
    {
        int n=j-i;
        if (n<4 || n>48) return false;
        // Полная вторая копия или вторая копия плюс зацикленное начало третьей.
        for(int length=n;length>=Math.Max(4,(n*3+3)/4);--length)
        {
            if (i<length) continue;
            int mismatches=0;
            for(int t=0;t<n;t++)
            {
                var left=a[i-length+t%length]; var right=a[i+t];
                if(left==right) continue;
                if(length<8 || left.Any(ch=>!char.IsLetter(ch) || ch<'а' || ch>'я') || right.Any(ch=>!char.IsLetter(ch) || ch<'а' || ch>'я') || Sensitive.IsMatch(left) || Sensitive.IsMatch(right)) { mismatches=2;break; }
                mismatches++;
            }
            if(mismatches<=1) return true;
        }
        return false;
    }

    // Минимальная пословная правка. Движемся слева: при дубле сохраняем первое вхождение.
    private static List<(int i,int j,int k,int l)> AnchorGaps(string[] a,string[] b)
    {
        var gaps = new List<(int,int,int,int)>();
        void Walk(int i,int j,int k,int l)
        {
            int max=0, ai=i, bi=k;
            var lengths=new int[l-k+1];
            for(int x=i;x<j;x++)
                for(int y=l-1;y>=k;y--)
                {
                    int n=lengths[y-k+1]=a[x]==b[y] ? lengths[y-k]+1 : 0;
                    if(n>max || (n==max && n>0 && x-n+1==ai && y-n+1<bi)) {max=n;ai=x-n+1;bi=y-n+1;}
                }
            if(max==0) {gaps.Add((i,j,k,l));return;}
            if(i<ai || k<bi) Walk(i,ai,k,bi);
            if(ai+max<j || bi+max<l) Walk(ai+max,j,bi+max,l);
        }
        Walk(0,a.Length,0,b.Length);
        return gaps;
    }

    private static List<(int i,int j,int k,int l)> Diff(string[] a,string[] b)
    {
        var d=new ushort[a.Length+1,b.Length+1];
        for(int i=a.Length;i>=0;--i) for(int k=b.Length;k>=0;--k)
            d[i,k]=(ushort)(i==a.Length ? b.Length-k : k==b.Length ? a.Length-i : a[i]==b[k] ? d[i+1,k+1] : 1+Math.Min(d[i+1,k+1],Math.Min(d[i+1,k],d[i,k+1])));
        var edits=new List<(int,int,int,int)>(); int x=0,y=0;
        while(x<a.Length || y<b.Length)
        {
            if(x<a.Length && y<b.Length && a[x]==b[y]) { x++;y++;continue; }
            int i=x,k=y;
            do {
                if(x<a.Length && y<b.Length && d[x,y]==1+d[x+1,y+1]) { x++;y++; }
                else if(x<a.Length && d[x,y]==1+d[x+1,y]) x++;
                else y++;
            } while((x<a.Length || y<b.Length) && !(x<a.Length && y<b.Length && a[x]==b[y]));
            edits.Add((i,x,k,y));
        }
        return edits;
    }
}
}
