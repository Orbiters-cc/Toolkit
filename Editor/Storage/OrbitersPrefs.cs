using System;
using UnityEditor;

namespace Orbiters.Toolkit.Editor.Storage
{
    /// <summary>
    /// Orbiters' editor settings, declared once with their key and default instead of raw EditorPrefs strings: a
    /// <see cref="User"/> setting follows the person on this computer across projects, a <see cref="Project"/> setting is
    /// this project's only (EditorPrefs under a key holding the project's path), a <see cref="Session"/> value lasts until
    /// the editor quits (SessionState: it survives script reloads). Keys are "Orbiters.&lt;Tool&gt;.&lt;Name&gt;".
    /// </summary>
    public static class OrbitersPrefs
    {
        public enum Scope { User, Project, Session }

        public static Pref<T> User<T>(string key, T fallback) => new Pref<T>(key, fallback, Scope.User);
        public static Pref<T> Project<T>(string key, T fallback) => new Pref<T>(key, fallback, Scope.Project);
        public static Pref<T> Session<T>(string key, T fallback) => new Pref<T>(key, fallback, Scope.Session);

        /// <summary>The key a project setting is stored under: <paramref name="key"/> and a hash of this project's folder.</summary>
        public static string ProjectKey(string key) => key + "@" + Hashing.Sha256(ProjectPaths.Root.Replace('\\', '/').ToLowerInvariant()).Substring(0, 12);

        /// <summary>One setting: bool, int, float or string.</summary>
        public sealed class Pref<T>
        {
            public readonly string Key;
            public readonly T Default;
            public readonly Scope Where;

            internal Pref(string key, T fallback, Scope where)
            {
                if (string.IsNullOrEmpty(key)) throw new ArgumentException("A setting needs a key.", nameof(key));
                if (typeof(T) != typeof(bool) && typeof(T) != typeof(int) && typeof(T) != typeof(float) && typeof(T) != typeof(string))
                    throw new NotSupportedException("Settings hold bool, int, float or string, not " + typeof(T).Name + ".");
                Key = key; Default = fallback; Where = where;
            }

            private string Stored => Where == Scope.Project ? ProjectKey(Key) : Key;

            public T Value
            {
                get
                {
                    string key = Stored;
                    object value;
                    if (Where == Scope.Session)
                    {
                        if (typeof(T) == typeof(bool)) value = SessionState.GetBool(key, (bool)(object)Default);
                        else if (typeof(T) == typeof(int)) value = SessionState.GetInt(key, (int)(object)Default);
                        else if (typeof(T) == typeof(float)) value = SessionState.GetFloat(key, (float)(object)Default);
                        else value = SessionState.GetString(key, (string)(object)Default);
                    }
                    else
                    {
                        if (!EditorPrefs.HasKey(key)) return Default;
                        if (typeof(T) == typeof(bool)) value = EditorPrefs.GetBool(key);
                        else if (typeof(T) == typeof(int)) value = EditorPrefs.GetInt(key);
                        else if (typeof(T) == typeof(float)) value = EditorPrefs.GetFloat(key);
                        else value = EditorPrefs.GetString(key);
                    }
                    return (T)value;
                }
                set
                {
                    string key = Stored;
                    object boxed = value;
                    if (Where == Scope.Session)
                    {
                        if (boxed is bool b) SessionState.SetBool(key, b);
                        else if (boxed is int i) SessionState.SetInt(key, i);
                        else if (boxed is float f) SessionState.SetFloat(key, f);
                        else if (boxed == null) SessionState.EraseString(key);
                        else SessionState.SetString(key, (string)boxed);
                        return;
                    }
                    if (boxed is bool bv) EditorPrefs.SetBool(key, bv);
                    else if (boxed is int iv) EditorPrefs.SetInt(key, iv);
                    else if (boxed is float fv) EditorPrefs.SetFloat(key, fv);
                    else if (boxed == null) EditorPrefs.DeleteKey(key);
                    else EditorPrefs.SetString(key, (string)boxed);
                }
            }

            /// <summary>Back to <see cref="Default"/>.</summary>
            public void Reset()
            {
                string key = Stored;
                if (Where != Scope.Session) { EditorPrefs.DeleteKey(key); return; }
                if (typeof(T) == typeof(bool)) SessionState.EraseBool(key);
                else if (typeof(T) == typeof(int)) SessionState.EraseInt(key);
                else if (typeof(T) == typeof(float)) SessionState.EraseFloat(key);
                else SessionState.EraseString(key);
            }
        }
    }
}
