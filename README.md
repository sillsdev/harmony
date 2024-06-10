# harmony

A CRDT application library for C#, use it to build offline first applications.

## Install

```sh
dotnet add package SIL.Harmony
```

It's expected that you use Harmony with the .Net IoC container ([IoC intro](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection)) and with EF Core. If you're not familier with that you can take a look at the [Host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host?tabs=hostbuilder) docs. If you're using ASP.NET Core you already have this setup for you.

#### Prerequsits:
* Setup EF Core in your application ([Getting started docs](https://learn.microsoft.com/en-us/ef/core/get-started/overview/first-app?tabs=netcore-cli))
* Setup a Host, the default host setup for ASP.NET Core will work, or a [generic host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host?tabs=hostbuilder) for desktop apps, depending on your app. Alterativly you could create a [`ServiceCollection`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection.servicecollection?view=net-8.0).

### Configure Db context
EF Core needs to be told about the entities used by Harmony, for now these are just [`Commit`](src/Crdt/Commit.cs), [`Snapshot`](src/Crdt/Db/ObjectSnapshot.cs), and [`ChangeEntitiy`](Crdt.Core/ChangeEntity.cs)
```C#
public class AppDbContext: DbContext {
  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
      modelBuilder.UseCrdt(crdtConfig.Value);
  }
}
```


> [!TIP]
> [`SampleDbContext`](src/Crdt.Sample/SampleDbContext.cs) has a full example of how to setup the DbContext.


### Register CRDT services
Harmony provides the [`DataModel`](src/Crdt/DataModel.cs) class as the main way the application will interact with the CRDT model. You first need to register it with the IoC container.
```C#
var builder = Host.CreateApplicationBuilder(args);
builder.Service.AddCrdtData<AppDbContext>(config => {});
```
> [!NOTE]
> the config callback passed into `AddCrdtData` is currently empty, we'll come back to that later.

> [!TIP]
> Pay attention to the generic type when calling `AddCrdtData`, this will be the type of your applications DB Context.

### Define CRDT objects
now that you have the services setup, you need to define a CRDT object. Take a look the following examples
* [`Word`](src/Crdt.Sample/Models/Word.cs) notice this contains a reference to an Antonym Word.
* [`Definition`](src/Crdt.Sample/Models/Definition.cs) references the Word it belongs to, notice that if the Word Reference is removed, the Definition deletes itself.
* [`Example`](src/Crdt.Sample/Models/Example.cs) this one is special because it uses a YDoc to store the example text in a [Yjs](https://github.com/yjs/yjs) compatible format. This allows the example sentence to be edited by multiple users and have those changes merged using the yjs CRDT algorithm.

Once you have created your CRDT object, you need to tell Harmony about it. Update the config callback passed into `AddCrdtData`
```C#
services.AddCrdtData<SampleDbContext>(config =>
{
// add the following lines
    config.ObjectTypeListBuilder
        .Add<Word>()
        .Add<Definition>()
        .Add<Example>();
});
```

### Define CRDT Changes
Now that we've defined our objects, we need to define our changes which will record user intent when making changes to objects. How detailed and specific you make your changes will directly impact how changes get merged between clients and how often users 'lose' changes that they made.

Example [`SetWordTextChange`](src/Crdt.Sample/Changes/SetWordTextChange.cs)
```C#
public class SetWordTextChange(Guid entityId, string text) : Change<Word>(entityId), ISelfNamedType<SetWordTextChange>
{
    public string Text { get; } = text;

    public override ValueTask<IObjectBase> NewEntity(Commit commit, ChangeContext context)
    {
        return new(new Word()
        {
            Id = EntityId,
            Text = Text
        });
    }


    public override ValueTask ApplyChange(Word entity, ChangeContext context)
    {
        entity.Text = Text;
        return ValueTask.CompletedTask;
    }
}
```
This is a fairly simple change, it can either create a new Word entry, or if the `entityId` passed in matches an object that has previously been created, then it will just set the `Text` field on the Word entry matching the Id.

> [!NOTE]
> Changes will be serilized and stored forever. Try to keep the amount of data stored as small as possible.
>
> This change can either create, or update an object. Most changes will probably be either an update, or a create. In those cases you should inherit from `EditChange<T>` or `CreateChange<T>`.

> [!TIP]
> The [Sample](src/Crdt.Sample/Changes) project contain a number of reference changes which are good examples for a couple different change types. There are also a built in [`DeleteChange<T>`](https://github.com/hahn-kev/harmony/blob/external-db-context/src/Crdt/Changes/DeleteChange.cs)

Once you have created your change types, you need to tell Harmony about them. Again update the config callback passed into `AddCrdtData`
```C#
services.AddCrdtData<SampleDbContext>(config =>
{
// add the following line
    config.ChangeTypeListBuilder.Add<SetWordTextChange>();
    config.ObjectTypeListBuilder
        .Add<Word>()
        .Add<Definition>()
        .Add<Example>();
});
```

### Use changes to author changes to CRDT objects

either via DI, or directly from the IoC container get an instance of [`DataModel`](src/Crdt/DataModel.cs) and call `AddChange`
```C#
Guid clientId = ... get a stable Guid representing the application instance
Guid objectId = Guid.NewGuid();
await dataModel.AddChange(
  clientId,
  new SetWordTextChange(objectId, "Hello World")
);
var word = await dataModel.GetLatest<Word>(objectId);
Console.WriteLine(word.Text);
```
> [!IMPORTANT]
> the `ClientId` should be consistent for a project per computer/device. It is used to determine what changes should be synced between clients with the assumption each client produces changes sequentially. So if the app is on 2 different computers both with the same project, each one should have a unique client Id, if they had the same Id, then they would not sync changes properly.
> 
> How the `ClientId` is stored is left up to the application, in FW Lite we created a table to store the ClientId, it's generated automatically when the project is downloaded or created the first time and it should never change after that.
>
> In case of a WebApp there could be one ClientId to just represent the server, however if users can author apps offline and sync them later, then each browser should get it's own ClientId

> [!WARNING]
> If you were to regenerate the `ClientId` for each change or on application start, then you will eventually have poor sync performance as the sync process checks if there's new changes to sync per `ClientId`

## Usage

### Queries
`DataModel` is the primary class for both making changes and getting data. We just showed an example of making changes, so we'll start with querying data.

Query Word objects starting with the letter "A"
```C#
DataModel dataModel; //get from IoC, probably via DI
var wordsStartingWithA = await dataModel.GetLatestObjects<Word>()
    .Where(w => w.Text.StartsWith("a"))
    .ToArrayAsync();
```
Harmony uses EF Core queries under the covers, you can read more about them [here](https://learn.microsoft.com/en-us/ef/core/querying/).

### Submitting Changes
Changes are the only way to modify CRDT data. Here's another example of a change
```C#
DataModel dataModel;
Guid clientId; //get a stable Guid representing the application instance
var definitionId = Guid.NewGuid();
Guid wordId; //get the word Id this definition is related to.
await dataModel.AddChange(clientId, new NewDefinitionChange(definitionId)
        {
            WordId = wordId,
            Text = "Hello",
            PartOfSpeech = partOfSpeech,
            Order = order
        });
```

> [!WARNING]
> You can modify data returned by EF Core, and issue updates and inserts yourself, however that data will be lost, and will not sync properly. Do not directly modify the tables produced by Harmony otherwise you risk losing data.

### Syncing data
Syncing is primarily done using the `DataModel` class, however the implementation of the server side is left up to you. You can find the Lexbox implemtnation [here](https://github.com/sillsdev/languageforge-lexbox/blob/develop/backend/LexBoxApi/Services/CrdtSyncRoutes.cs). The sync works by having 2 instances of the ISyncable interface. The local  one is implemented by `DataModel` however the remote implementation depends on your server side, the FW Liate implementation can be found [here](https://github.com/sillsdev/languageforge-lexbox/blob/eefe404ab90593a2a36185f705babe0bdbcfd0d6/backend/LocalWebApp/CrdtHttpSyncService.cs#L63), you will need to scope the instance to the project and it will need to deal with authentication also.

once you have a remote representation of the `ISyncable` interface you just call it like this
```C#
DataModel dataModel;
ISyncable remoteModel;
await dataModel.SyncWith(remoteModel);
```
it's pretty simple, all the heavy lifting is done by the interface which is fairly simple to implement.
