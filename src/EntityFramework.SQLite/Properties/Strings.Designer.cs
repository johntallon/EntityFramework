// <auto-generated />
namespace Microsoft.Data.Entity.Sqlite
{
    using System.Diagnostics;
    using System.Globalization;
    using System.Reflection;
    using System.Resources;
	using JetBrains.Annotations;

    public static class Strings
    {
        private static readonly ResourceManager _resourceManager
            = new ResourceManager("EntityFramework.SQLite.Strings", typeof(Strings).GetTypeInfo().Assembly);

        /// <summary>
        /// The string argument '{argumentName}' cannot be empty.
        /// </summary>
        public static string ArgumentIsEmpty([CanBeNull] object argumentName)
        {
            return string.Format(CultureInfo.CurrentCulture, GetString("ArgumentIsEmpty", "argumentName"), argumentName);
        }

        /// <summary>
        /// The value provided for argument '{argumentName}' must be a valid value of enum type '{enumType}'.
        /// </summary>
        public static string InvalidEnumValue([CanBeNull] object argumentName, [CanBeNull] object enumType)
        {
            return string.Format(CultureInfo.CurrentCulture, GetString("InvalidEnumValue", "argumentName", "enumType"), argumentName, enumType);
        }

        /// <summary>
        /// '{generatorType}' does not support migration operations of type '{operationType}'.
        /// </summary>
        public static string MigrationOperationNotSupported([CanBeNull] object generatorType, [CanBeNull] object operationType)
        {
            return string.Format(CultureInfo.CurrentCulture, GetString("MigrationOperationNotSupported", "generatorType", "operationType"), generatorType, operationType);
        }

        /// <summary>
        /// SQLite-specific methods can only be used when the context is using a SQLite data store.
        /// </summary>
        public static string SqliteNotInUse
        {
            get { return GetString("SqliteNotInUse"); }
        }

        private static string GetString(string name, params string[] formatterNames)
        {
            var value = _resourceManager.GetString(name);

            Debug.Assert(value != null);

            if (formatterNames != null)
            {
                for (var i = 0; i < formatterNames.Length; i++)
                {
                    value = value.Replace("{" + formatterNames[i] + "}", "{" + i + "}");
                }
            }

            return value;
        }
    }
}
