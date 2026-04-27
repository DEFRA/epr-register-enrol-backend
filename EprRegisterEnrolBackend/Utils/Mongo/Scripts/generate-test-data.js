import { generateOrganisation } from './data-generators.js'
import { MongoClient } from 'mongodb'
import { createOrganisationCollection } from './create-collection.js'

const TOTAL_ORGANISATIONS = 20
const REGISTRATION_ACCREDITATION_COUNT = 3
const COLLECTION_NAME = 'organisation_epr'
const DB_NAME = 'epr'
// Use localhost when running locally, mongo when running in Docker
const MONGO_URL = process.env.MONGO_URL || 'mongodb://localhost:27017'

async function insertDocuments(uri, dbName, collectionName, documents) {
  const client = new MongoClient(uri)
  try {
    await client.connect()
    const collection = client.db(dbName).collection(collectionName)

    console.log(`Inserting ${documents.length} organisations...`)
    await collection.insertMany(documents)
    console.log(`✓ Successfully inserted ${documents.length} organisations`)

    const count = await collection.countDocuments()
    console.log(`Total documents in collection: ${count}`)
  } 
  
  catch(e) {
        console.log(e);
    }

  finally {
    await client.close()
  }



}

const organisations = Array.from({ length: TOTAL_ORGANISATIONS }, (_, i) =>
  generateOrganisation(500000 + i, REGISTRATION_ACCREDITATION_COUNT)
)

console.log('Starting MongoDB initialization...')
console.log(`Database: ${DB_NAME}`)
console.log(`Collection: ${COLLECTION_NAME}`)
console.log(`MongoDB URL: ${MONGO_URL}`)

await createOrganisationCollection(MONGO_URL, DB_NAME, COLLECTION_NAME)

await insertDocuments(MONGO_URL, DB_NAME, COLLECTION_NAME, organisations)

console.log('✓ MongoDB initialization completed successfully')
