# Out of Scope

1. Admin Portal. All residents and amenities are seeded and can't be updated.
2. Scheduled job reopening amenities after reservations end.


# Entities and their Requirements and Assumptions

## Residents
- The only "Users"
- Basic info, name, email, phone number, apt number, building_id

### Relationships:
- 1..n with Reservations

## Reservations
- Reservations have a size. Total size of all reservations in a given slot must be less than amenity capacity.
- Each reservation lasts 1 hour (00 - 59) so that they cannot overlap.
- Resident may create consecutive reservations at the same amenity as long as capacity is not breached.

### Relationships:
- n..1 with Residents
- 1..1 with Amenities

## Amenities
- Name, total_capacity, reservation_capacity, opening time, closing time, open (boolean), max size per reservation, building_id
- Some amenities can fill by number of reservations (reservation_capacity), total capacity or both (pool cabanas).
- All rules for amenities not specified in TASK.md must be added as columns to the Amenities to table to keep logic simple.

### Relationships:
- 1..1 with Amenities


# Questions for PM
1. How is capacity for an amenity defined and do different amenities have different capacity requirements? I've made an assumption that both total users and number of reservations can be involved.
2. Do residents need to be able to sign up or is that handled by the admin?
3. What are our security concerns? Is this exposed to the internet or only on the buildings intranet?
4. Do residents only connect via the web or do we also support an app, or are we thinking about building an app soon?
5. Do we want a way to send messages back to residents? Maybe the pool unexpectedly closes, or a reservation gets cancelled.


# API

GET /amenities
Request: {}
Response: 
[
    {
       Id, Name, total_capacity, reservation_capacity, max_size_per_reservation, opening time, closing time, open (boolean)
    }
]

GET /amenities/{amenity_id}/availability
Request: {}
Response: [
    start_time,
    end_time,
    available: boolean
]

POST /resident/{resident_id}/amenity/{amenity_id}/reservation
Request: {
    resident_id,
    amenity_id,
    start_time,
    size
}

Response: {
    success: boolean
}

DELETE /resident/{resident_id}/amenity/{amenity_id}/reservation/{reservation_id}
Request: {
}
Response: {
    success: boolean
}

# Multitenancy / Isolation
- Each resident and amenity includes their building id and a resident can only see / interact with amenities with a matching building_id.

# Doublebooking Prevention:
- "Doublebooking" is underspecified so I made the assumption that each amenity has a capacity defined by 3 properties:
total_capacity (total number of residents at amenity at one time), reservation_capacity (total number of reservations at one time),
and max_size_per_reservation (maximum "size" value that any one reservation can have). I chose to do it this way because it allows me to keep all of the constraints within the amenities table, and I can apply them to every amenity instead of needing to worry about special cases. An amenity can have a 0 for any of them if it doesn't apply.
I specifically chose to keep all reservations 1 hour long in order to not need to handle overlapping bookings of different lengths.
If an amenity is not available, it will have available: false in the response to GET /amenities/{amenity_id}/availability and the user will not be able to select it. If an amenity is available, but the user's reservation would exceed the size available, the user gets a 4xx response with a message that tells them what size is allowed for that slot.
- Creating a reservation needs to lock all other reservations for that amenity to prevent a race condition