var builder = DistributedApplication.CreateBuilder(args);

var rmq = builder.AddRabbitMQ("rmq")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithManagementPlugin();

var db = builder.AddPostgres("postgres")
    .WithPgAdmin()
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("app-db");

builder.AddProject<Projects.CodeLab_WolverineOutAndInBox_Api>("codelab-wolverineoutandinbox-api")
    .WithReference(rmq)
    .WithReference(db)
    .WaitFor(rmq)
    .WaitFor(db);

builder.Build().Run();
