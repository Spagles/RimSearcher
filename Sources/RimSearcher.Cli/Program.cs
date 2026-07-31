using System.Text;
using ConsoleAppFramework;
using RimSearcher.Cli.Commands;
using RimSearcher.Cli.Infrastructure;
using RimSearcher.Cli.Queries;

Console.OutputEncoding = Encoding.UTF8;

string databasePath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "defs.db");
var connectionFactory = new DatabaseConnectionFactory(databasePath);
var output = new JsonOutput();
var app = ConsoleApp.Create();
var defRepository = new DefRepository(connectionFactory);
SearchCommands.Register(app, defRepository, output);
DefCommands.Register(app, defRepository, output);
FieldCommands.Register(app, new FieldRepository(connectionFactory), output);
StatisticsCommands.Register(app, new StatisticsRepository(connectionFactory), output);
MaintenanceCommands.Register(app);

app.Run(args);
