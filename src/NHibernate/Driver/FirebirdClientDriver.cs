using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NHibernate.Dialect;
using NHibernate.SqlCommand;
using NHibernate.SqlTypes;
using NHibernate.Util;

namespace NHibernate.Driver
{
	/// <summary>
	/// A NHibernate Driver for using the Firebird data provider located in
	/// <c>FirebirdSql.Data.FirebirdClient</c> assembly.
	/// </summary>
	public class FirebirdClientDriver : ReflectionBasedDriver
	{
		private const string SELECT_CLAUSE_EXP = @"(?<=\bselect|\bwhere).*";
		private const string CAST_PARAMS_EXP = @"(?<![=<>]\s?|first\s?|skip\s?|between\s|between\s@\bp\w+\b\sand\s)@\bp\w+\b(?!\s?[=<>])";
		private readonly Regex _statementRegEx = new Regex(SELECT_CLAUSE_EXP, RegexOptions.IgnoreCase);
		private readonly Regex _castCandidateRegEx = new Regex(CAST_PARAMS_EXP, RegexOptions.IgnoreCase);
		private readonly FirebirdDialect _fbDialect = new FirebirdDialect();

		/// <summary>
		/// Initializes a new instance of the <see cref="FirebirdClientDriver"/> class.
		/// </summary>
		/// <exception cref="HibernateException">
		/// Thrown when the <c>FirebirdSql.Data.Firebird</c> assembly can not be loaded.
		/// </exception>
		public FirebirdClientDriver()
			: base(
				"FirebirdSql.Data.FirebirdClient",
				"FirebirdSql.Data.FirebirdClient",
				"FirebirdSql.Data.FirebirdClient.FbConnection",
				"FirebirdSql.Data.FirebirdClient.FbCommand")
		{

		}

		public override void Configure(IDictionary<string, string> settings)
		{
			base.Configure(settings);
			_fbDialect.Configure(settings);
		}

		public override bool UseNamedPrefixInSql
		{
			get { return true; }
		}

		public override bool UseNamedPrefixInParameter
		{
			get { return true; }
		}

		public override string NamedPrefix
		{
			get { return "@"; }
		}

		protected override void InitializeParameter(DbParameter dbParam, string name, SqlType sqlType)
		{
			var convertedSqlType = sqlType;
			if (convertedSqlType.DbType == DbType.Currency)
				convertedSqlType = SqlTypeFactory.Decimal;

			base.InitializeParameter(dbParam, name, convertedSqlType);
		}

		public override DbCommand GenerateCommand(CommandType type, SqlString sqlString, SqlType[] parameterTypes)
		{
			var command = base.GenerateCommand(type, sqlString, parameterTypes);

			var expWithParams = GetStatementsWithCastCandidates(command.CommandText);
			if (!string.IsNullOrWhiteSpace(expWithParams))
			{
				var candidates = GetCastCandidates(expWithParams);

				var index = 0;
				foreach (DbParameter p in command.Parameters)
				{
					if (candidates.Contains(p.ParameterName))
						TypeCastParam(p, command, parameterTypes[index]);
					index++;
				}
			}

			return command;
		}

		private string GetStatementsWithCastCandidates(string commandText)
		{
			return _statementRegEx.Match(commandText).Value;
		}

		private HashSet<string> GetCastCandidates(string statement)
		{
			var candidates =
				_castCandidateRegEx
					.Matches(statement)
					.Cast<Match>()
					.Select(match => match.Value);
			return new HashSet<string>(candidates);
		}

		private void TypeCastParam(DbParameter param, DbCommand command, SqlType sqlType)
		{
			var castType = GetFbTypeForParam(sqlType);
			command.CommandText = command.CommandText.ReplaceWholeWord(
				param.ParameterName,
				$"cast({param.ParameterName} as {castType})");
		}

		private string GetFbTypeForParam(SqlType sqlType)
		{
			if (sqlType.LengthDefined)
				switch (sqlType.DbType)
				{
					case DbType.AnsiString:
					case DbType.String:
						// Use default length instead for supporting like expressions requiring longer length.
						sqlType = new SqlType(sqlType.DbType);
						break;
				}
			return _fbDialect.GetCastTypeName(sqlType);
		}

		private static volatile MethodInfo _clearPool;
		private static volatile MethodInfo _clearAllPools;

		/// <summary>
		/// Clears the connection pool.
		/// </summary>
		/// <param name="connectionString">The connection string of connections for which to clear the pool.
		/// <c>null</c> for clearing them all.</param>
		public void ClearPool(string connectionString)
		{
			// In case of concurrent threads, may initialize many times. We do not care.
			// Members are volatile for avoiding it gets used while its constructor is not yet ended.
			if (_clearPool == null || _clearAllPools == null)
			{
				using (var clearConnection = CreateConnection())
				{
					var connectionType = clearConnection.GetType();
					_clearPool = connectionType.GetMethod("ClearPool") ?? throw new InvalidOperationException("Unable to resolve ClearPool method.");
					_clearAllPools = connectionType.GetMethod("ClearAllPools") ?? throw new InvalidOperationException("Unable to resolve ClearAllPools method.");
				}
			}

			if (connectionString != null)
			{
				using (var clearConnection = CreateConnection())
				{
					clearConnection.ConnectionString = connectionString;
					_clearPool.Invoke(null, new object[] {clearConnection});
				}
				return;
			}

			_clearAllPools.Invoke(null, new object[0]);
		}

		/// <summary>
		/// This driver support of <see cref="System.Transactions.Transaction"/> is not compliant and too heavily
		/// restricts what can be done for NHibernate tests. See DNET-764, DNET-766 (and bonus, DNET-765).
		/// </summary>
		/// <remarks>
		/// <list type="bullet">
		/// <item>
		/// <term>DNET-764</term>
		/// <description>When auto-enlistment is enabled (<c>Enlist=true</c> in connection string), the driver throws if
		/// attempting to open a connection without an ambient transaction. http://tracker.firebirdsql.org/browse/DNET-764
		/// </description>
		/// </item>
		/// <item>
		/// <term>DNET-765</term>
		/// <description>When the connection string does not specify auto-enlistment parameter <c>Enlist</c>, the driver
		/// defaults to <c>false</c>. http://tracker.firebirdsql.org/browse/DNET-765
		/// </description>
		/// </item>
		/// <item>
		/// <term>DNET-766</term>
		/// <description>When auto-enlistment is disabled (<c>Enlist=false</c> in connection string), the driver ignores
		/// calls to <see cref="DbConnection.EnlistTransaction(System.Transactions.Transaction)"/>. They silently do
		/// nothing, the Firebird connection does not get enlisted. http://tracker.firebirdsql.org/browse/DNET-766
		/// </description>
		/// </item>
		/// </list>
		/// </remarks>
		public override bool SupportsSystemTransactions => false;

		/// <summary>
		/// <see langword="false"/>. Enlistment is completely disabled when auto-enlistment is disabled.
		/// See http://tracker.firebirdsql.org/browse/DNET-766.
		/// </summary>
		public override bool SupportsEnlistmentWhenAutoEnlistmentIsDisabled => false;
	}
}
