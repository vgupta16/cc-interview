import {
  AMENITY,
  BUILDING_A,
  BUILDING_B,
  bookViaForm,
  expect,
  gotoApp,
  openAmenity,
  selectBuilding,
  test,
  ui,
} from './helpers';

const testIds = (cards: import('@playwright/test').Locator) =>
  cards.evaluateAll((els) => els.map((e) => e.getAttribute('data-testid') ?? '').sort());

test.describe('tenant isolation', () => {
  test("S10 Building B does not show alice's Building A booking and lists different amenities", async ({
    page,
    scenario,
  }) => {
    await gotoApp(page);

    // Building A: three amenity cards, book the Party Room.
    await expect(ui.amenityCard(page, AMENITY.partyRoomA)).toBeVisible();
    await expect(ui.allAmenityCards(page)).toHaveCount(3);
    const idsA = await testIds(ui.allAmenityCards(page));
    await openAmenity(page, AMENITY.partyRoomA);
    await bookViaForm(page, { start: scenario.at(10), minutes: 60, resident: 'alice' });

    // Building B: switching reloads the list; open its Party Room.
    await selectBuilding(page, BUILDING_B);
    await expect(ui.amenityCard(page, AMENITY.partyRoomB)).toBeVisible();
    await expect(ui.amenityCard(page, AMENITY.partyRoomA)).toHaveCount(0);
    await expect(ui.allAmenityCards(page)).toHaveCount(3);
    const idsB = await testIds(ui.allAmenityCards(page));

    expect(idsB).toEqual(
      [AMENITY.partyRoomB, AMENITY.gymB, AMENITY.guestParkingB].map((id) => `amenity-card-${id}`).sort(),
    );
    expect(idsA).toEqual(
      [AMENITY.partyRoomA, AMENITY.gymA, AMENITY.guestParkingA].map((id) => `amenity-card-${id}`).sort(),
    );
    expect(idsB.filter((id) => idsA.includes(id))).toEqual([]); // no shared amenity ids

    await openAmenity(page, AMENITY.partyRoomB, { waitForList: true });
    await expect(ui.allRows(page)).toHaveCount(0);
    await expect(ui.rowsOf(page, 'alice')).toHaveCount(0);

    // Sanity: going back to Building A still shows the booking (it was not lost or moved).
    await selectBuilding(page, BUILDING_A);
    await openAmenity(page, AMENITY.partyRoomA, { waitForList: true });
    await expect(ui.rowsOf(page, 'alice')).toHaveCount(1);
  });
});
