﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using XPloit.Server.Http;
using XPloit.Server.Http.Enums;
using XPloit.Server.Http.Interfaces;
using XPloit.Sniffer.Enums;
using XPloit.Sniffer.Interfaces;
using XPloit.Sniffer.Streams;

namespace XPloit.Sniffer.Extractors
{
    public class ExtractHttp : IObjectExtractor
    {
        static string[] SqliWordList = new string[] { "concat(", "unionselect", "sysobjects", "1'='1", "version(", "@@version", "mysql.user", "xp_cmdshell", "groupby", "information_schema" };
        static string[] XssWordList = new string[] { "<script", "onerror=", "onmouseover=", "document.cookie", "javascript:" };

        static string[] UserWordList = new string[] {/* "u",*/ "login", "uid", "id", "userid", "user_id", "user", "uname", "username", "user_name", "usuario", "mail", "email", "name", "user_login", "email_login" };
        static string[] PasswordWordList = new string[] {/* "p",*/ "pass", "password", "key", "pwd", "clave", "hash", "password_login" };

        public class HttpAttack : Attack
        {
            public HttpAttack() : base(EAttackType.None) { }
            public HttpAttack(EAttackType type, DateTime date, IPEndPoint ip) : base(date, ip, type) { }
            /// <summary>
            /// Host
            /// </summary>
            public string HttpHost { get; set; }
            /// <summary>
            /// Url
            /// </summary>
            public string HttpUrl { get; set; }
            /// <summary>
            /// User
            /// </summary>
            public string[] Get { get; set; }
            /// <summary>
            /// Password
            /// </summary>
            public string[] Post { get; set; }
        }
        public class HttpCredential : Credential
        {
            public HttpCredential() : base(ECredentialType.None) { }
            public HttpCredential(ECredentialType type, DateTime date, IPEndPoint ip) : base(date, ip, type) { }
            /// <summary>
            /// Host
            /// </summary>
            public string HttpHost { get; set; }
            /// <summary>
            /// Url
            /// </summary>
            public string HttpUrl { get; set; }
            /// <summary>
            /// User
            /// </summary>
            public string[] User { get; set; }
            /// <summary>
            /// Password
            /// </summary>
            public string[] Password { get; set; }
        }
        public class HttpAuthCredential : Credential
        {
            public HttpAuthCredential() : base(ECredentialType.HttpAuth) { }
            public HttpAuthCredential(DateTime date, IPEndPoint ip) : base(date, ip, ECredentialType.HttpAuth) { }
            /// <summary>
            /// Host
            /// </summary>
            public string HttpHost { get; set; }
            /// <summary>
            /// Url
            /// </summary>
            public string HttpUrl { get; set; }
            /// <summary>
            /// User
            /// </summary>
            public string User { get; set; }
            /// <summary>
            /// Password
            /// </summary>
            public string Password { get; set; }
        }

        enum EDic
        {
            User,
            Pass,
            SQLI,
            XSS
        }

        static IObjectExtractor _Current = new ExtractHttp();
        public static IObjectExtractor Current { get { return _Current; } }

        class server : IHttpServer, IEnumerable<HttpRequest>, IDisposable
        {
            List<HttpRequest> _List = new List<HttpRequest>();
            public bool AllowKeepAlive { get { return true; } }
            public uint MaxPost { get { return uint.MaxValue; } }
            public uint MaxPostMultiPart { get { return uint.MaxValue; } }
            public bool ProgreesOnAllPost { get { return false; } }
            public bool SessionCheckFakeId { get { return false; } }
            public string SessionCookieName { get { return null; } }
            public string SessionDirectory { get { return null; } }
            public bool GetAndPostNamesToLowerCase { get { return true; } }

            IEnumerator IEnumerable.GetEnumerator() { return _List.GetEnumerator(); }
            public IEnumerator<HttpRequest> GetEnumerator() { return _List.GetEnumerator(); }

            public void OnPostProgress(HttpPostProgress post_pg, EHttpPostState start) { }
            public void OnRequest(HttpProcessor httpProcessor) { _List.Add(httpProcessor.Request); }
            public void OnRequestSocket(HttpProcessor httpProcessor) { }
            public bool PushAdd(HttpProcessor httpProcessor, string push_code) { return false; }
            public void PushDel(HttpProcessor httpProcessor, string push_code) { }

            public void Dispose() { _List.Clear(); }
        }

        public EExtractorReturn GetObjects(TcpStream stream, out object[] cred)
        {
            if (!stream.IsClossed)
            {
                cred = null;
                if (stream.FirstStream != null && stream.FirstStream.Emisor != ETcpEmisor.Client) return EExtractorReturn.DontRetry;

                if (stream.ClientLength > 30)
                {
                    if (!stream.FirstStream.DataAscii.Contains(" HTTP/"))
                    {
                        if (!stream.FirstStream.DataAscii.StartsWith("GET ") &&
                            !stream.FirstStream.DataAscii.StartsWith("POST "))
                            return EExtractorReturn.DontRetry;
                    }
                }

                return EExtractorReturn.Retry;
            }

            if (stream.ClientLength < 30)
            {
                cred = null;
                return EExtractorReturn.DontRetry;
            }

            List<object> ls = new List<object>();
            foreach (TcpStreamMessage pack in stream)
                if (pack.Emisor == ETcpEmisor.Client)
                {
                    if (!pack.DataAscii.Contains("Host: "))
                    {
                        cred = null;
                        return EExtractorReturn.DontRetry;
                    }

                    using (server s = new server())
                    using (MemoryStream str = new MemoryStream(pack.Data))
                    using (HttpProcessor p = new HttpProcessor(stream.Destination.ToString(), str, s, true))
                    {
                        foreach (HttpRequest r in s)
                        {
                            string next = pack.Next == null ? null : pack.Next.DataAscii.Split('\n').FirstOrDefault();

                            bool valid = !string.IsNullOrEmpty(next) && next.Contains(" 200 ") && next.StartsWith("HTTP");

                            if (r.Autentication != null)
                            {
                                ls.Add(new HttpAuthCredential(stream.StartDate, stream.Destination)
                                {
                                    IsValid = valid,
                                    HttpHost = r.Host.ToString(),
                                    HttpUrl = r.Url,
                                    Password = r.Autentication.Password,
                                    User = r.Autentication.User
                                });
                            }

                            if (r.Files.Length > 0)
                            {

                            }

                            for (int x = 0; x < 2; x++)
                            {
                                Dictionary<string, string> d = x == 0 ? r.GET : r.POST;

                                if (d.Count <= 0) continue;

                                List<string> pwds = new List<string>();
                                List<string> users = new List<string>();
                                if (Fill(d, pwds, EDic.Pass) && Fill(d, users, EDic.User))
                                {
                                    users.Sort();
                                    pwds.Sort();
                                    ls.Add(new HttpCredential(
                                        (x == 0 ? Credential.ECredentialType.HttpGet : Credential.ECredentialType.HttpPost),
                                        stream.StartDate, stream.Destination)
                                    {
                                        IsValid = valid,
                                        HttpHost = r.Host.ToString(),
                                        HttpUrl = r.Url,
                                        User = users.Count == 0 ? null : users.ToArray(),
                                        Password = pwds.Count == 0 ? null : pwds.ToArray()
                                    });
                                }
                            }

                            foreach (EDic attack in new EDic[] { EDic.SQLI, EDic.XSS })
                            {
                                if (Fill(r.GET, attack) || Fill(r.POST, attack))
                                {
                                    Attack.EAttackType type;
                                    switch (attack)
                                    {
                                        case EDic.SQLI: type = Attack.EAttackType.HttpSqli; break;
                                        case EDic.XSS: type = Attack.EAttackType.HttpXss; break;
                                        default: continue;
                                    }

                                    ls.Add(new HttpAttack(type, stream.StartDate, stream.Destination)
                                    {
                                        HttpHost = r.Host.ToString(),
                                        HttpUrl = r.Url,
                                        Get = r.GET.Count == 0 ? null : ToDic(r.GET).OrderBy(c => c).ToArray(),
                                        Post = r.POST.Count == 0 ? null : ToDic(r.POST).OrderBy(c => c).ToArray(),
                                    });
                                }
                            }
                        }
                    }
                }

            cred = ls.Count == 0 ? null : ls.ToArray();
            return cred == null ? EExtractorReturn.DontRetry : EExtractorReturn.True;
        }
        IEnumerable<string> ToDic(Dictionary<string, string> g)
        {
            foreach (KeyValuePair<string, string> val in g)
                yield return val.Key + "=" + val.Value;
        }

        bool ExcludeUserPass(string val, EDic dic)
        {
            if (string.IsNullOrEmpty(val)) return true;
            if (val.StartsWith("http://", StringComparison.InvariantCultureIgnoreCase)) return true;
            if (val.StartsWith("https://", StringComparison.InvariantCultureIgnoreCase)) return true;

            switch (dic)
            {
                case EDic.User:
                    {
                        if (val.Length > 100) return true;
                        if (val == "0") return true;

                        break;
                    }
                case EDic.Pass:
                    {
                        if (val.Length > 130) return true;
                        break;
                    }
            }
            return false;
        }
        bool Fill(Dictionary<string, string> d, List<string> ls, EDic dic)
        {
            if (d.Count == 0) return false;

            bool ret = false;
            string v;
            foreach (string su in GetDic(dic))
            {
                if (d.TryGetValue(su, out v) && !ls.Contains(v) && !ExcludeUserPass(v, dic))
                {
                    ls.Add(v);
                    ret = true;
                }
            }
            return ret;
        }
        bool Fill(Dictionary<string, string> d, EDic dic)
        {
            if (d.Count == 0) return false;

            foreach (string su in GetDic(dic))
            {
                foreach (KeyValuePair<string, string> val in d)
                {
                    string sval = val.Value.ToLowerInvariant();

                    if (sval.Replace(" ", "").Contains(su))
                        return true;
                }
            }
            return false;
        }
        string[] GetDic(EDic dic)
        {
            switch (dic)
            {
                case EDic.User: return UserWordList;
                case EDic.Pass: return PasswordWordList;
                case EDic.SQLI: return SqliWordList;
                case EDic.XSS: return XssWordList;
            }

            return null;
        }
    }
}