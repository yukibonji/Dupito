﻿module Program

open System
open System.Configuration
open System.Data
open System.IO
open System.Security.Cryptography
open FirebirdSql.Data.FirebirdClient
open Microsoft.FSharp.Collections

let dbLocation () =
    let dbfile = ConfigurationManager.AppSettings.["DbLocation"]
    if dbfile <> null 
        then dbfile
        else Path.GetDirectoryName AppDomain.CurrentDomain.BaseDirectory

let dbFilename = Path.Combine (dbLocation(), "dupito.db")
let connectionStringForFile = sprintf "Database=%s;ServerType=1;User=SYSDBA;Password=masterkey"
let connectionString = connectionStringForFile dbFilename

let createConn() = 
    let conn = new FbConnection(connectionString)
    conn.Open()
    conn :> IDbConnection

/// Connection manager
let cmgr = Sql.withNewConnection createConn

// partial application of common functions
let db = SqlWrapper cmgr
let P = Sql.Parameter.make

let createDB() =
    let exec sql = db.ExecNonQuery sql [] |> ignore
    FbConnection.CreateDatabase connectionString
    exec "create table filehash (id int not null primary key, filepath varchar(1000), hash varchar(100))"
    exec "CREATE GENERATOR gen"
    exec "CREATE TRIGGER bt FOR filehash \
BEFORE INSERT \
AS \
BEGIN \
  IF (NEW.id IS NULL) \
    THEN NEW.id = GEN_ID(gen, 1); \
END "
    for key in ["filepath"; "hash"] do
        exec (sprintf "create index IX_%s on filehash(%s)" key key)

let setupDB () = 
    if not (File.Exists dbFilename) then
        createDB()
    ()

let help () =
    printfn "a: add files in this directory and subdirectories to database"
    printfn "c: cleanup database"
    printfn "l: list dupes"
    printfn "r: rehash files in database"
    printfn "d: deletes duplicate files interactively"
    printfn "dd: deletes duplicate files automatically"
    0

/// Hash a file
let hashFile f = 
    printfn "hashing %s" f
    use fs = new FileStream(f, FileMode.Open)
    use hashFunction = new SHA512Managed()
    hashFunction.ComputeHash fs |> Convert.ToBase64String

/// Hash a stream (async)
let hashAsync bufferSize (hashFunction: HashAlgorithm) (stream: Stream) progressReport =
    let rec hashBlock currentBlock count = async {
        progressReport stream.Position
        let buffer = Array.zeroCreate<byte> bufferSize
        let! readCount = stream.AsyncRead buffer
        if readCount = 0 then
            hashFunction.TransformFinalBlock(currentBlock, 0, count) |> ignore
        else 
            hashFunction.TransformBlock(currentBlock, 0, count, currentBlock, 0) |> ignore
            return! hashBlock buffer readCount
    }
    async {
        let buffer = Array.zeroCreate<byte> bufferSize
        let! readCount = stream.AsyncRead buffer
        do! hashBlock buffer readCount
        return hashFunction.Hash |> Convert.ToBase64String
    }


let consolelock = obj()
/// print to console, locking it to avoid parallel overlapping
let lprintfn t = 
    let flush (a: string) = 
        System.Console.WriteLine(a)
        //printfn "%s" a
        System.Console.Out.Flush()
    lock consolelock (fun () -> Printf.kprintf flush t)

/// Hash a file async
let hashFileAsync f =
    let bufferSize = 65536
    async {
        use! fs = File.AsyncOpenRead f
        use hashFunction = new SHA512Managed()
        let total = fs.Length
        let report (pos: int64) = lprintfn "%s: %s" f (((double pos)/(double total)).ToString("P"))
        return! hashAsync bufferSize hashFunction fs ignore
    }

type FileHash = {
    id: int
    Hash: string
    FilePath: string
}        

let getId (h: FileHash) = h.id
let getHash (h: FileHash) = h.Hash
let getFilepath (h: FileHash) = h.FilePath
let getFilesize (f: string) = (FileInfo(f)).Length
    
/// Hash a file and save it to database
let indexFile (save : FileHash -> unit) f = 
    lprintfn "Indexing file %A" f
    try
        let hash = hashFile f
        save {id = 0; Hash = hash; FilePath = f}
        lprintfn "Finished indexing file %A" f
    with e -> 
        lprintfn "Exception: %s\n%s" e.Message e.StackTrace
        ()

/// Hash a file and save it to database (async)
let indexFileAsync (save: FileHash -> unit) f =
    async {
        lprintfn "Indexing file %A" f
        try
            let! hash = hashFileAsync f
            save {id = 0; Hash = hash; FilePath = f}
            lprintfn "Finished indexing file %A" f
        with e -> 
            lprintfn "Exception: %s\n%s" e.Message e.StackTrace
            ()
    }

let enumerateAllFiles() = 
    Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.*", SearchOption.AllDirectories)

let toUpper (s: string) = s.ToUpperInvariant()

let add (fileHashEnumerate : unit -> FileHash seq) (fileHashSave : FileHash -> unit) =
    let allFiles = enumerateAllFiles() |> Seq.map toUpper
    let filesInDb = fileHashEnumerate() |> Seq.map (getFilepath >> toUpper) |> Seq.toList
    let filesToInsert = allFiles |> Seq.except filesInDb
    filesToInsert
    //|> Seq.sortBy (fun x -> - getFilesize x)
    |> PSeq.iter (indexFile fileHashSave)
    0

let cleanup (fileHashEnumerate : unit -> FileHash seq) delete =
    fileHashEnumerate() 
    |> Seq.filter (getFilepath >> File.Exists >> not)
    |> Seq.iter delete
    0

/// Maps raw data record to FileHash
let asFileHash = Sql.asRecord<FileHash>

/// Compares pairs disregarding order
let comparePairs (x1,y1) (x2,y2) = (x1 = x2 && y1 = y2) || (x1 = y2 && y1 = x2)

let applyToPair f (x,y) = (f x, f y)

let getIds = applyToPair getId
let getHashes = applyToPair getHash
let getFilepaths = applyToPair getFilepath
let getFiledate (h: FileHash) = (FileInfo(h.FilePath)).LastWriteTime

/// Gets all dupes in database
let getDupes () =
    let fields = Sql.recordFieldsAlias typeof<FileHash>
    let sql = sprintf "select %s,%s from filehash a \
                       join filehash b on a.hash = b.hash \
                       where a.filepath <> b.filepath" 
                        (fields "a") (fields "b")
    db.ExecReader sql []
    |> Sql.map (fun r -> asFileHash "a" r, asFileHash "b" r)
    |> Seq.distinctWith (fun x y -> comparePairs (getHashes x) (getHashes y))
    |> Seq.groupBy (fst >> getHash)
    |> Seq.map (snd >> Seq.map fst >> Seq.distinctBy getFilepath >> Seq.sortBy getFiledate)

let printDupeFilenames =
    Seq.distinctWith (fun x y -> comparePairs (getFilepaths x) (getFilepaths y))
    >> Seq.groupBy (fst >> getFilepath)
    >> Seq.iter (snd >> Seq.map fst >> Seq.nth 0 >> getFilepath >> printfn "file: %s")

let deleteDupeFilenames() =
    let fields = Sql.recordFieldsAlias typeof<FileHash>
    let sql = sprintf "select %s,%s from filehash a \
                       join filehash b on a.filepath = b.filepath \
                       where a.id <> b.id" 
                        (fields "a") (fields "b")
    db.ExecReader sql []
    |> Sql.map (fun r -> asFileHash "a" r, asFileHash "b" r)
    |> printDupeFilenames
    0

/// Prints all dupes in database
let printList () =
    getDupes()
    |> Seq.map (Seq.map getFilepath >> Seq.toArray >> String.concat "\n")
    |> Seq.iter (printfn "dupes:\n%s\n")
    0

let findAll () =
    db.ExecReader "select * from filehash" [] |> Sql.map (asFileHash "")

let save (f: FileHash) =
    db.ExecNonQueryF "insert into filehash (hash, filepath) values (%s, %s)"
        f.Hash f.FilePath
        |> ignore

let deleteByPath f =
    db.ExecNonQueryF "delete from filehash where filepath = %s" f |> ignore

let delete (f: FileHash) = deleteByPath f.FilePath

let rehash () =
    failwith "not implemented"
    0

let deleteInteractively () =
    failwith "not implemented"
    0

let deleteWithoutAsking () =
    getDupes()
    |> Seq.collect (Seq.skip 1)
    |> Seq.map getFilepath
    |> Seq.iter ([printfn "deleting %s"; File.Delete; deleteByPath] |> Seq.iterf)
    0

let printAll() =
    findAll()
    |> Seq.iter (fun h -> printfn "%s\t%s" h.FilePath h.Hash)
    0

let findDupes() =
    let allFiles = enumerateAllFiles()
    let filesBySize = 
        allFiles
        |> Seq.groupBy getFilesize
        |> Seq.map snd
    let filesByHash = 
        filesBySize
        |> Seq.map Seq.cache
        |> Seq.cache
        |> PSeq.filter (fun x -> Seq.length x > 1)
        |> PSeq.map (Seq.groupBy hashFile)
        |> PSeq.map (Seq.map snd)
        |> PSeq.collect id
        |> PSeq.filter (fun x -> Seq.length x > 1)
    filesByHash
    |> Seq.iter (fun dupes ->
                        printfn "\nDupes:"
                        dupes |> Seq.iter (printfn "%s"))
    0

[<EntryPoint>]
let main args = 
    setupDB()
    if args.Length = 0
        then help()
        else match args.[0] with
             | "a" -> add findAll save
             | "c" -> cleanup findAll delete
             | "f" -> findDupes()
             | "l" -> printList()
             | "p" -> printAll()
             | "r" -> rehash()
             | "d" -> deleteInteractively()
             | "dd" -> deleteWithoutAsking()
             | "z" -> deleteDupeFilenames()
             | _ -> help ()